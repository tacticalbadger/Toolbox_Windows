using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Toolbox.Support;

/// <summary>
/// The file work of an update: checking a downloaded package against what
/// the signed feed said about it, unpacking it, and putting the new files in
/// the place of the old ones.
///
/// The swap is done by the new version itself. The running app cannot
/// replace its own files, so it starts the freshly unpacked copy with
/// <c>--apply-update</c> and closes; that copy waits for it to be gone,
/// moves the old files aside, copies its own in, and starts the app from its
/// usual folder. If anything fails half-way the old files are put back.
/// </summary>
internal static class UpdateInstaller
{
    /// <summary>The list of a package's files, shipped inside it. An update removes what the old list names and the new one does not.</summary>
    public const string ManifestName = "files.txt";

    public const string Executable = "Toolbox.exe";

    /// <summary>Whether <paramref name="file"/> is the package the feed described: same length, same SHA-256.</summary>
    public static bool Matches(string file, UpdatePackage package)
    {
        var info = new FileInfo(file);
        if (!info.Exists || info.Length != package.Size) return false;
        using var stream = File.OpenRead(file);
        var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        return CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(hash), System.Text.Encoding.ASCII.GetBytes(package.Sha256));
    }

    /// <summary>
    /// Unpacks a verified package into <paramref name="into"/> (emptied
    /// first) and returns the folder that holds the app: the package's one
    /// top-level folder.
    /// </summary>
    public static string Unpack(string zip, string into)
    {
        if (Directory.Exists(into)) Directory.Delete(into, recursive: true);
        Directory.CreateDirectory(into);
        // Refuses entries that would land outside the folder.
        ZipFile.ExtractToDirectory(zip, into);
        var app = File.Exists(Path.Combine(into, Executable))
            ? into
            : Directory.GetDirectories(into).FirstOrDefault(d => File.Exists(Path.Combine(d, Executable)));
        if (app is null || !File.Exists(Path.Combine(app, "toolbox_core.dll")))
        {
            throw new InvalidDataException("the package does not contain the app");
        }
        return app;
    }

    /// <summary>Whether files can be created in <paramref name="folder"/>: an update has to write there.</summary>
    public static bool CanWrite(string folder)
    {
        try
        {
            var probe = Path.Combine(folder, $".toolbox-update-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The files below <paramref name="folder"/>, as paths relative to it.</summary>
    public static List<string> FilesOf(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(folder, f))
            .ToList();

    /// <summary>
    /// The files a version put into its folder, from its manifest. Anything
    /// in it that is not a plain path below the folder is left out: the
    /// list decides what gets moved away.
    /// </summary>
    public static List<string> ReadManifest(string folder)
    {
        var path = Path.Combine(folder, ManifestName);
        if (!File.Exists(path)) return [];
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var files = new List<string>();
        foreach (var line in File.ReadLines(path))
        {
            var relative = line.Trim().Replace('/', Path.DirectorySeparatorChar);
            if (relative.Length == 0 || Path.IsPathRooted(relative)) continue;
            var full = Path.GetFullPath(Path.Combine(folder, relative));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            files.Add(Path.GetRelativePath(folder, full));
        }
        return files;
    }

    /// <summary>
    /// Replaces the app in <paramref name="installed"/> with the one in
    /// <paramref name="fresh"/>. The old version's files (its manifest, plus
    /// everything the new version brings a file for) are moved to
    /// <paramref name="aside"/> first; on any failure they are moved back
    /// and the exception is passed on. Files that neither version knows
    /// stay where they are.
    /// </summary>
    public static void Replace(string installed, string fresh, string aside)
    {
        var incoming = FilesOf(fresh);
        var outgoing = ReadManifest(installed).Concat(incoming).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(f => File.Exists(Path.Combine(installed, f))).ToList();
        if (Directory.Exists(aside)) Directory.Delete(aside, recursive: true);
        Directory.CreateDirectory(aside);

        var moved = new List<string>();
        var copied = new List<string>();
        try
        {
            foreach (var file in outgoing)
            {
                var to = Path.Combine(aside, file);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                Retry(() => File.Move(Path.Combine(installed, file), to));
                moved.Add(file);
            }
            foreach (var file in incoming)
            {
                var to = Path.Combine(installed, file);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(Path.Combine(fresh, file), to, overwrite: false);
                copied.Add(file);
            }
        }
        catch (Exception)
        {
            // Back to what was there: nothing new stays, everything old returns.
            foreach (var file in copied) TryDelete(Path.Combine(installed, file));
            foreach (var file in moved)
            {
                try { File.Move(Path.Combine(aside, file), Path.Combine(installed, file), overwrite: true); }
                catch (Exception e) { Log.Error($"updates: could not put {file} back", e); }
            }
            throw;
        }
    }

    /// <summary>The old process may still hold a file for a moment after it is gone, and so may a virus scanner.</summary>
    private static void Retry(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 20)
            {
                Thread.Sleep(250);
            }
        }
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (Exception) { /* reported by whoever looks at the folder next */ }
    }

    /// <summary>
    /// What the freshly unpacked copy does when started with
    /// <c>--apply-update &lt;installed folder&gt; &lt;process id&gt;</c>: waits for the
    /// old app to be gone, swaps the files, and starts the app from its
    /// usual folder (the new one, or the old one again if the swap failed).
    /// Returns a sentence for the user when it failed.
    /// </summary>
    public static string? Apply(string installed, int oldProcess)
    {
        var fresh = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var aside = Path.Combine(Path.GetDirectoryName(fresh) ?? fresh, "previous");
        string? problem = null;
        try
        {
            installed = Path.GetFullPath(installed);
            if (!File.Exists(Path.Combine(installed, Executable))) throw new InvalidOperationException($"there is no Toolbox in {installed}");
            if (string.Equals(installed.TrimEnd(Path.DirectorySeparatorChar), fresh, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("the update was started from the folder it is meant to replace");
            }
            WaitForExit(oldProcess, installed);
            Log.Info($"updates: installing {AppInfo.Display} into {installed}");
            Replace(installed, fresh, aside);
            Log.Info($"updates: installed {AppInfo.Display}");
        }
        catch (Exception e)
        {
            Log.Error("updates: the update could not be installed; the version that was there is back", e);
            problem = $"Toolbox {AppInfo.Version} could not be installed, and the version you had is still there.\n\n{e.Message}";
        }
        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(installed, Executable)) { WorkingDirectory = installed, UseShellExecute = false })?.Dispose();
        }
        catch (Exception e)
        {
            Log.Error("updates: Toolbox could not be started again", e);
            problem ??= "The update is installed, but Toolbox could not be started again: " + e.Message;
        }
        return problem;
    }

    private static void WaitForExit(int processId, string installed)
    {
        Process old;
        try { old = Process.GetProcessById(processId); }
        catch (ArgumentException) { return; } // gone already
        using (old)
        {
            // The number may belong to another program by now.
            string? path;
            try { path = old.MainModule?.FileName; }
            catch (Exception) { return; }
            if (!string.Equals(path, Path.Combine(installed, Executable), StringComparison.OrdinalIgnoreCase)) return;
            if (!old.WaitForExit(60_000)) throw new TimeoutException("the running Toolbox did not close");
        }
    }
}
