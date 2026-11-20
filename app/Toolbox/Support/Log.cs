using System.Diagnostics;
using System.IO;
using System.Text;

namespace Toolbox.Support;

/// <summary>
/// A small append-only log file. Secrets never go in here: callers log what
/// happened and to which host or record, not passwords, keys or key material.
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();
    private const long MaxBytes = 2 * 1024 * 1024;
    private static bool prepared;

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}: {exception.GetType().Name}: {exception.Message}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}";
        Debug.Write(line);
        lock (Gate)
        {
            try
            {
                Prepare();
                File.AppendAllText(AppPaths.LogPath, line, Encoding.UTF8);
            }
            catch (Exception)
            {
                // A log that cannot be written must not take the app down.
            }
        }
    }

    /// <summary>Keeps the previous log as .old once the file grows past the limit.</summary>
    private static void Prepare()
    {
        if (prepared) return;
        prepared = true;
        var path = AppPaths.LogPath;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var info = new FileInfo(path);
        if (info.Exists && info.Length > MaxBytes)
        {
            File.Move(path, path + ".old", overwrite: true);
        }
    }
}
