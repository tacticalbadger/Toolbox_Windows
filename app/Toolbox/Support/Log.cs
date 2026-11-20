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

    /// <summary>
    /// One entry is one line. Messages can carry text from the other end of a
    /// connection (an error a server sent, a name from a directory); a line
    /// break in it must not be able to pass for an entry of its own.
    /// </summary>
    internal static string OneLine(string message)
    {
        if (!message.Any(char.IsControl)) return message;
        var builder = new StringBuilder(message.Length + 8);
        foreach (var c in message)
        {
            builder.Append(c switch
            {
                '\r' => "\\r",
                '\n' => "\\n",
                '\t' => "\t",
                _ when char.IsControl(c) => $"\\u{(int)c:x4}",
                _ => c.ToString(),
            });
        }
        return builder.ToString();
    }

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {OneLine(message)}{Environment.NewLine}";
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
