using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Windows.Security.Credentials.UI;

namespace Toolbox.Model;

/// <summary>
/// "Is the person at the keyboard the owner of this Windows session?"
/// Windows Hello (face, fingerprint or PIN) answers when it is set up.
/// Where it is not, the standard Windows Security dialog asks for the
/// account's password and Windows checks it. Either way the check is done by
/// Windows; Toolbox never stores what was entered.
/// </summary>
internal static class OwnerCheck
{
    /// <summary>Returns when verified. Throws <see cref="VaultException"/> on cancel or when no check is possible.</summary>
    public static async Task VerifyAsync(string message)
    {
        var owner = Prompts.Owner();
        var window = owner is null ? IntPtr.Zero : new WindowInteropHelper(owner).Handle;

        var availability = UserConsentVerifierAvailability.DeviceNotPresent;
        try
        {
            availability = await UserConsentVerifier.CheckAvailabilityAsync();
        }
        catch (Exception e)
        {
            Log.Warn($"owner check: Windows Hello availability could not be read: {e.Message}");
        }

        if (availability == UserConsentVerifierAvailability.Available)
        {
            Log.Info("owner check: asking Windows Hello");
            var result = window != IntPtr.Zero
                ? await UserConsentVerifierInterop.RequestVerificationForWindowAsync(window, message)
                : await UserConsentVerifier.RequestVerificationAsync(message);
            switch (result)
            {
                case UserConsentVerificationResult.Verified:
                    Log.Info("owner check: verified by Windows Hello");
                    return;
                case UserConsentVerificationResult.Canceled:
                    throw VaultException.Cancelled();
                case UserConsentVerificationResult.RetriesExhausted:
                    throw VaultException.Unavailable("Windows Hello stopped after too many attempts.");
                case UserConsentVerificationResult.DeviceBusy:
                    throw VaultException.Unavailable("Windows Hello is busy; try again in a moment.");
                default:
                    // Hello went away between the check and the request: use the password.
                    Log.Warn($"owner check: Windows Hello answered {result}; asking for the password instead");
                    break;
            }
        }
        else
        {
            Log.Info($"owner check: Windows Hello is {availability}; asking for the account password");
        }

        await VerifyWithPasswordAsync(window, message);
        Log.Info("owner check: verified by account password");
    }

    // ----- Account password through the Windows Security dialog -----

    private const int ErrorCancelled = 1223;
    private const int ErrorLogonFailure = 1326;
    private const int ErrorAccountRestriction = 1327;
    private const int ErrorLogonTypeNotGranted = 1385;
    private const uint CredUiWinEnumerateCurrentUser = 0x200;
    private const uint CredPackProtectedCredentials = 0x1;
    private const int LogonInteractive = 2;
    private const int LogonNetwork = 3;
    private const int MaxAttempts = 3;

    private static async Task VerifyWithPasswordAsync(IntPtr owner, string message)
    {
        var user = Environment.UserName;
        var domain = Environment.UserDomainName;
        var shown = string.IsNullOrEmpty(domain) ? user : $@"{domain}\{user}";
        var info = new CredUiInfo
        {
            Size = Marshal.SizeOf<CredUiInfo>(),
            Parent = owner,
            Message = message,
            Caption = "Toolbox",
        };

        // Pre-fill the dialog with this account, so only the password is asked for.
        var inBuffer = IntPtr.Zero;
        uint inSize = 0;
        CredPackAuthenticationBufferW(0, shown, "", IntPtr.Zero, ref inSize);
        if (inSize > 0)
        {
            inBuffer = Marshal.AllocCoTaskMem((int)inSize);
            if (!CredPackAuthenticationBufferW(0, shown, "", inBuffer, ref inSize))
            {
                Marshal.FreeCoTaskMem(inBuffer);
                inBuffer = IntPtr.Zero;
                inSize = 0;
            }
        }

        try
        {
            var lastError = 0;
            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                uint package = 0;
                var save = false;
                var status = CredUIPromptForWindowsCredentialsW(ref info, lastError, ref package, inBuffer, inSize,
                    out var outBuffer, out var outSize, ref save, CredUiWinEnumerateCurrentUser);
                if (status == ErrorCancelled) throw VaultException.Cancelled();
                if (status != 0) throw VaultException.Unavailable(new Win32Exception(status).Message);

                int outcome;
                try
                {
                    // Checking against a domain controller can take a moment; keep the window responsive.
                    var buffer = outBuffer;
                    var size = outSize;
                    outcome = await Task.Run(() => CheckPassword(buffer, size, user, domain));
                }
                finally
                {
                    Wipe(outBuffer, (int)outSize);
                    Marshal.FreeCoTaskMem(outBuffer);
                }

                if (outcome == 0) return;
                if (outcome != ErrorLogonFailure)
                {
                    throw VaultException.Unavailable(new Win32Exception(outcome).Message);
                }
                lastError = outcome; // the dialog comes back saying the password was wrong
            }
            throw VaultException.Unavailable("the password was not accepted.");
        }
        finally
        {
            if (inBuffer != IntPtr.Zero) Marshal.FreeCoTaskMem(inBuffer);
        }
    }

    /// <summary>
    /// Has Windows check the password entered in the dialog against the
    /// account Toolbox runs as. Only the password is taken from the dialog:
    /// whatever account name it showed, nobody else's password passes.
    /// Returns 0 on success, otherwise a Win32 error code.
    /// </summary>
    private static int CheckPassword(IntPtr authBuffer, uint authSize, string user, string domain)
    {
        uint userChars = 0, domainChars = 0, passwordChars = 0;
        // The first call only reports the buffer sizes (in characters).
        CredUnPackAuthenticationBufferW(CredPackProtectedCredentials, authBuffer, authSize,
            IntPtr.Zero, ref userChars, IntPtr.Zero, ref domainChars, IntPtr.Zero, ref passwordChars);

        var userBytes = (int)(userChars + 1) * 2;
        var domainBytes = (int)(domainChars + 1) * 2;
        var passwordBytes = (int)(passwordChars + 1) * 2;
        var userBuffer = Marshal.AllocHGlobal(userBytes);
        var domainBuffer = Marshal.AllocHGlobal(domainBytes);
        var passwordBuffer = Marshal.AllocHGlobal(passwordBytes);
        Wipe(passwordBuffer, passwordBytes);
        try
        {
            userChars += 1;
            domainChars += 1;
            passwordChars += 1;
            if (!CredUnPackAuthenticationBufferW(CredPackProtectedCredentials, authBuffer, authSize,
                    userBuffer, ref userChars, domainBuffer, ref domainChars, passwordBuffer, ref passwordChars))
            {
                return Marshal.GetLastWin32Error();
            }

            // An interactive logon also works from cached credentials when no
            // domain controller is reachable. Hardened PCs may forbid one logon
            // type for some accounts, so the network type is tried as well.
            var error = TryLogon(user, domain, passwordBuffer, LogonInteractive);
            if (error == ErrorLogonTypeNotGranted)
            {
                error = TryLogon(user, domain, passwordBuffer, LogonNetwork);
            }
            // An account without a password is refused for every scripted
            // logon type; an empty entry is the correct answer for it.
            if (error == ErrorAccountRestriction && Marshal.ReadInt16(passwordBuffer) == 0)
            {
                error = 0;
            }
            return error;
        }
        finally
        {
            Wipe(passwordBuffer, passwordBytes);
            Marshal.FreeHGlobal(passwordBuffer);
            Marshal.FreeHGlobal(domainBuffer);
            Marshal.FreeHGlobal(userBuffer);
        }
    }

    private static int TryLogon(string user, string domain, IntPtr password, int logonType)
    {
        if (LogonUserW(user, string.IsNullOrEmpty(domain) ? "." : domain, password, logonType, 0, out var token))
        {
            CloseHandle(token);
            return 0;
        }
        return Marshal.GetLastWin32Error();
    }

    private static unsafe void Wipe(IntPtr buffer, int bytes)
    {
        if (buffer != IntPtr.Zero && bytes > 0) new Span<byte>((void*)buffer, bytes).Clear();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CredUiInfo
    {
        public int Size;
        public IntPtr Parent;
        [MarshalAs(UnmanagedType.LPWStr)] public string Message;
        [MarshalAs(UnmanagedType.LPWStr)] public string Caption;
        public IntPtr Banner;
    }

    [DllImport("credui.dll", CharSet = CharSet.Unicode)]
    private static extern int CredUIPromptForWindowsCredentialsW(
        ref CredUiInfo info, int authError, ref uint authPackage,
        IntPtr inAuthBuffer, uint inAuthBufferSize,
        out IntPtr outAuthBuffer, out uint outAuthBufferSize,
        [MarshalAs(UnmanagedType.Bool)] ref bool save, uint flags);

    [DllImport("credui.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredPackAuthenticationBufferW(
        uint flags, string userName, string password, IntPtr packedCredentials, ref uint packedCredentialsSize);

    [DllImport("credui.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredUnPackAuthenticationBufferW(
        uint flags, IntPtr authBuffer, uint authBufferSize,
        IntPtr userName, ref uint maxUserName,
        IntPtr domainName, ref uint maxDomainName,
        IntPtr password, ref uint maxPassword);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogonUserW(
        string userName, string domain, IntPtr password, int logonType, int logonProvider, out IntPtr token);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
