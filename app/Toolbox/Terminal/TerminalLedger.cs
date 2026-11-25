using System.Diagnostics;
using System.IO;

namespace Toolbox.Terminal;

/// <summary>
/// The shells behind this run's local terminals, written down so that a
/// later run can end the ones that were left without a window.
///
/// A terminal in a pane is a child window of the app. When the app is
/// killed outright (not closed: killed, or crashed), Windows destroys that
/// window with it, and Windows Terminal keeps the shell running all the
/// same, with no window anybody could reach it through (seen 2026-11-25).
/// Nothing can be done at that moment. So each run keeps a small file
/// naming itself and its terminals' shells, and at start the files of runs
/// that are gone are read: a shell still running from one of them is ended,
/// with what it started. A process is only taken for the one written down
/// if its id, start time and name all match.
/// </summary>
internal static class TerminalLedger
{
    private static readonly string Folder = Path.Combine(AppPaths.LocalDirectory, "terminals");
    private static readonly string Mine = Path.Combine(Folder, $"{Environment.ProcessId}.txt");
    private static readonly List<Entry> Shells = [];

    private readonly record struct Entry(int Id, long Started, string Name)
    {
        public string Line(string kind) => $"{kind} {Id} {Started} {Name}";
    }

    /// <summary>Notes a terminal's shell. Does nothing if the process cannot be looked at.</summary>
    public static void Add(int shell)
    {
        if (Describe(shell) is not { } entry) return;
        Shells.RemoveAll(s => s.Id == shell);
        Shells.Add(entry);
        Save();
    }

    /// <summary>The terminal is gone, or is a window of its own again: its shell is nobody's to end.</summary>
    public static void Remove(int shell)
    {
        if (Shells.RemoveAll(s => s.Id == shell) > 0) Save();
    }

    /// <summary>
    /// At start: ends the shells that earlier runs left behind when they
    /// were killed. A run that is still going is left alone, and so is
    /// everything it started.
    /// </summary>
    public static void EndOrphans()
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            foreach (var file in Directory.GetFiles(Folder, "*.txt"))
            {
                if (string.Equals(file, Mine, StringComparison.OrdinalIgnoreCase)) continue;
                var entries = File.ReadAllLines(file).Select(Parse).ToList();
                var owner = entries.FirstOrDefault(e => e.Kind == "owner").Entry;
                if (owner.Id != 0 && IsStillThe(owner)) continue;
                foreach (var (_, shell) in entries.Where(e => e.Kind == "shell"))
                {
                    if (!IsStillThe(shell)) continue;
                    try
                    {
                        using var process = Process.GetProcessById(shell.Id);
                        process.Kill(entireProcessTree: true);
                        Log.Info($"local terminal: ended {shell.Name} ({shell.Id}), left without a window when an earlier run was killed");
                    }
                    catch (Exception e)
                    {
                        Log.Warn($"local terminal: {shell.Name} ({shell.Id}) was left without a window and could not be ended: {e.Message}");
                    }
                }
                File.Delete(file);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"local terminal: the record of earlier terminals could not be read: {e.Message}");
        }
    }

    private static void Save()
    {
        try
        {
            if (Shells.Count == 0)
            {
                File.Delete(Mine);
                return;
            }
            Directory.CreateDirectory(Folder);
            var lines = new List<string>();
            if (Describe(Environment.ProcessId) is { } me) lines.Add(me.Line("owner"));
            lines.AddRange(Shells.Select(s => s.Line("shell")));
            File.WriteAllLines(Mine, lines);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"local terminal: the record of this run's terminals could not be written: {e.Message}");
        }
    }

    private static Entry? Describe(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return new Entry(processId, process.StartTime.ToUniversalTime().Ticks, process.ProcessName);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsStillThe(Entry entry) =>
        Describe(entry.Id) is { } now && now.Started == entry.Started &&
        string.Equals(now.Name, entry.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>"shell 1234 638…ticks powershell"; anything else is no entry.</summary>
    private static (string Kind, Entry Entry) Parse(string line)
    {
        var parts = line.Split(' ', 4);
        if (parts.Length == 4 && parts[0] is "owner" or "shell" &&
            int.TryParse(parts[1], out var id) && id > 0 && long.TryParse(parts[2], out var started) && parts[3].Length is > 0 and < 200)
        {
            return (parts[0], new Entry(id, started, parts[3]));
        }
        return ("", default);
    }
}
