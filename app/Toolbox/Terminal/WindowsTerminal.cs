using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Toolbox.Terminal;

/// <summary>A profile of the user's Windows Terminal: a name and nothing else the app needs.</summary>
internal sealed record TerminalProfile(string Name);

/// <summary>A process as the system lists it.</summary>
internal readonly record struct ProcessEntry(int Id, int ParentId, string Name);

/// <summary>
/// The user's own Windows Terminal, which local terminal panes show: where
/// it is, which profiles it has, how a new window of it is opened, and what
/// runs in one.
///
/// A pane does not start the shell itself. It asks Windows Terminal for a
/// new window (in "focus" mode, which has neither tabs nor a title bar) and
/// moves that window into the pane, so everything about it is the user's
/// Terminal: profiles, colours, fonts, key bindings.
/// </summary>
internal static class WindowsTerminal
{
    private const string WindowClass = "CASCADIA_HOSTING_WINDOW_CLASS";

    // One window is asked for at a time: the new window is told apart from
    // the ones that were there before, which only works for one at once.
    private static readonly SemaphoreSlim Opening = new(1, 1);

    /// <summary>
    /// wt.exe, or null where Windows Terminal is not installed. It is the
    /// alias Windows keeps for the installed app, wherever that is.
    /// </summary>
    public static string? Executable { get; } = FindExecutable();

    private static string? FindExecutable()
    {
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                 "Microsoft", "WindowsApps", "wt.exe");
        // An alias is a reparse point: it is there, but cannot be opened as a file.
        try { if (File.GetAttributes(alias) != (FileAttributes)(-1)) return alias; }
        catch (Exception) { /* not there */ }
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(folder.Trim(), "wt.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception) { /* a PATH entry that is not a path */ }
        }
        return null;
    }

    // ----- Profiles -----

    /// <summary>
    /// The profiles Windows Terminal shows in its own menu, in its order, as
    /// far as its settings file names them. Profiles it adds by itself (a
    /// new WSL distribution, say) appear once Terminal has written them to
    /// the file. Empty when the file is not there or cannot be read: the
    /// default profile still works without it.
    /// </summary>
    public static IReadOnlyList<TerminalProfile> Profiles()
    {
        foreach (var path in SettingsPaths())
        {
            try
            {
                if (!File.Exists(path)) continue;
                return ParseProfiles(File.ReadAllText(path));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                Log.Warn($"local terminal: Windows Terminal's settings could not be read: {e.Message}");
            }
        }
        return [];
    }

    private static IEnumerable<string> SettingsPaths()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(local, "Packages", "Microsoft.WindowsTerminal_8wekyb3d8bbwe", "LocalState", "settings.json");
        yield return Path.Combine(local, "Packages", "Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe", "LocalState", "settings.json");
        yield return Path.Combine(local, "Microsoft", "Windows Terminal", "settings.json");
    }

    /// <summary>The visible profiles named in a Windows Terminal settings file (JSON with comments).</summary>
    internal static IReadOnlyList<TerminalProfile> ParseProfiles(string settings)
    {
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        using var document = JsonDocument.Parse(settings, options);
        if (!document.RootElement.TryGetProperty("profiles", out var profiles)) return [];
        // Either an object with a "list", or (older files) the list itself.
        var list = profiles.ValueKind == JsonValueKind.Object && profiles.TryGetProperty("list", out var inner) ? inner : profiles;
        if (list.ValueKind != JsonValueKind.Array) return [];
        var found = new List<TerminalProfile>();
        foreach (var profile in list.EnumerateArray())
        {
            if (profile.ValueKind != JsonValueKind.Object) continue;
            if (profile.TryGetProperty("hidden", out var hidden) && hidden.ValueKind == JsonValueKind.True) continue;
            if (!profile.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String) continue;
            var text = name.GetString()?.Trim();
            // A name is shown in a menu and handed to wt.exe as one argument.
            if (string.IsNullOrEmpty(text) || text.Length > 100 || text.Any(char.IsControl)) continue;
            if (found.All(p => p.Name != text)) found.Add(new TerminalProfile(text));
        }
        return found;
    }

    // ----- Opening a window -----

    /// <summary>What <see cref="OpenWindowAsync"/> found: the window, and the process it belongs to.</summary>
    internal readonly record struct OpenedWindow(nint Handle, int ProcessId, int? ShellProcessId);

    /// <summary>
    /// Asks Windows Terminal for a new window with <paramref name="profile"/>
    /// (null: its default profile) and returns it, still hidden when all
    /// goes well. Null when no window appeared.
    /// </summary>
    public static async Task<OpenedWindow?> OpenWindowAsync(string? profile, CancellationToken cancel)
    {
        if (Executable is not { } wt) return null;
        await Opening.WaitAsync(cancel);
        try
        {
            var before = Windows().ToHashSet();
            var processesBefore = Processes().Select(p => p.Id).ToHashSet();
            var start = new ProcessStartInfo(wt) { UseShellExecute = false, CreateNoWindow = true };
            // A window of its own ("new"), without tabs and title bar ("focus").
            start.ArgumentList.Add("-w");
            start.ArgumentList.Add("new");
            start.ArgumentList.Add("--focus");
            if (profile is not null)
            {
                start.ArgumentList.Add("new-tab");
                start.ArgumentList.Add("-p");
                start.ArgumentList.Add(profile);
            }
            using (Process.Start(start)) { }

            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(15))
            {
                await Task.Delay(40, cancel);
                var window = Windows().FirstOrDefault(w => !before.Contains(w));
                if (window == 0) continue;
                GetWindowThreadProcessId(window, out var processId);
                var shell = await FindShellAsync((int)processId, processesBefore, cancel);
                return new OpenedWindow(window, (int)processId, shell);
            }
            return null;
        }
        finally
        {
            Opening.Release();
        }
    }

    /// <summary>
    /// The shell Windows Terminal started for the new window: a process of
    /// its own that was not there before and is not one of its console
    /// hosts. It may take a moment to appear; null if it does not.
    /// </summary>
    private static async Task<int?> FindShellAsync(int terminalProcess, HashSet<int> before, CancellationToken cancel)
    {
        for (var attempt = 0; attempt < 25; attempt++)
        {
            var shell = Processes().FirstOrDefault(p => p.ParentId == terminalProcess && !before.Contains(p.Id) && !IsConsoleHost(p.Name));
            if (shell.Id != 0) return shell.Id;
            await Task.Delay(40, cancel);
        }
        return null;
    }

    private static bool IsConsoleHost(string name) =>
        name.Equals("OpenConsole.exe", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("conhost.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What runs in a shell besides the shell itself: the name of a program
    /// it started that is still there, or null for an idle shell (or one
    /// that has gone).
    /// </summary>
    public static string? RunningIn(int shellProcess)
    {
        var all = Processes();
        if (all.All(p => p.Id != shellProcess)) return null;
        var child = all.LastOrDefault(p => p.ParentId == shellProcess && !IsConsoleHost(p.Name));
        return child.Id == 0 ? null : ProgramName(child.Name);
    }

    /// <summary>"PING.EXE" as a person would say it: "ping". Names that are not all capitals keep their spelling.</summary>
    internal static string ProgramName(string executable)
    {
        var name = Path.GetFileNameWithoutExtension(executable);
        return name.Any(char.IsLetter) && name == name.ToUpperInvariant() ? name.ToLowerInvariant() : name;
    }

    public static bool IsRunning(int process) => Processes().Any(p => p.Id == process);

    private static List<nint> Windows()
    {
        var found = new List<nint>();
        var name = new StringBuilder(64);
        EnumWindows((window, _) =>
        {
            name.Clear();
            if (GetClassName(window, name, name.Capacity) > 0 && name.ToString() == WindowClass) found.Add(window);
            return true;
        }, 0);
        return found;
    }

    /// <summary>Every process, with its parent, in the order the system lists them (oldest first as a rule).</summary>
    internal static List<ProcessEntry> Processes()
    {
        var found = new List<ProcessEntry>();
        var snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot == InvalidHandle) return found;
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snapshot, ref entry)) return found;
            do
            {
                found.Add(new ProcessEntry((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, entry.szExeFile));
            }
            while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return found;
    }

    // ----- Win32 -----

    private const uint SnapshotProcesses = 0x00000002;
    private static readonly nint InvalidHandle = -1;

    private delegate bool EnumWindowsProc(nint window, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder name, int capacity);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(nint snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(nint snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nuint th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }
}
