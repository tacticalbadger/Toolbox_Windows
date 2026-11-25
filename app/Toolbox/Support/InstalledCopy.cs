using System.IO;
using Microsoft.Win32;

namespace Toolbox.Support;

/// <summary>
/// What Windows' list of installed apps says about this copy, for a copy
/// the installer put there (tools\installer\Toolbox.iss).
///
/// The installer writes the version it installed. The app then updates
/// itself by replacing its own files, with no installer involved, and the
/// list would go on showing the version that was first installed. So at
/// start the app writes its own version there, if the entry is about the
/// folder it runs from. A copy that was unzipped somewhere, a build made
/// for trying things, or a copy beside the installed one finds no entry of
/// its own and writes nothing.
/// </summary>
internal static class InstalledCopy
{
    /// <summary>The installer's AppId. The same in tools\installer\Toolbox.iss; never changed.</summary>
    public const string AppId = "{89DB47A7-75BF-4E60-83DB-D1927B1EB6AD}";

    /// <summary>Under HKEY_CURRENT_USER: Inno Setup names the key after the AppId.</summary>
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppId + "_is1";

    /// <summary>At start: makes the installed-apps entry of this copy, if it has one, name this version.</summary>
    public static void RecordVersion()
    {
        try
        {
            if (RecordVersion(Registry.CurrentUser, UninstallKey, AppContext.BaseDirectory, AppInfo.Version))
            {
                Log.Info($"installed copy: Windows' list of installed apps now says {AppInfo.Version}");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Warn($"installed copy: the version in Windows' list of installed apps could not be updated: {e.Message}");
        }
    }

    /// <summary>
    /// Writes <paramref name="version"/> into the entry at <paramref name="keyPath"/>,
    /// if there is one, it is about <paramref name="folder"/>, and it says
    /// something else. Returns whether anything was written.
    /// </summary>
    public static bool RecordVersion(RegistryKey root, string keyPath, string folder, string version)
    {
        using var key = root.OpenSubKey(keyPath, writable: true);
        if (key is null) return false;
        if (key.GetValue("InstallLocation") is not string installed || !SameFolder(installed, folder)) return false;
        if (key.GetValue("DisplayVersion") as string == version) return false;
        key.SetValue("DisplayVersion", version, RegistryValueKind.String);
        // The same number in parts, under both spellings the installer uses.
        if (System.Version.TryParse(version, out var parsed))
        {
            foreach (var name in new[] { "MajorVersion", "VersionMajor" }) key.SetValue(name, parsed.Major, RegistryValueKind.DWord);
            foreach (var name in new[] { "MinorVersion", "VersionMinor" }) key.SetValue(name, parsed.Minor, RegistryValueKind.DWord);
        }
        return true;
    }

    private static bool SameFolder(string one, string other)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(one)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(other)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
