using System.IO;

namespace Toolbox.Support;

/// <summary>
/// Where the app keeps its files. The TOOLBOX_* environment overrides point a
/// run at a throwaway setup (the end-to-end fixture) instead of the real data.
/// </summary>
internal static class AppPaths
{
    /// <summary>%APPDATA%\Toolbox: connections, vault, vault key, settings.</summary>
    public static string DataDirectory { get; } = Ensure(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Toolbox"));

    /// <summary>%LOCALAPPDATA%\Toolbox: logs and the WebView2 profile (machine-local, never roams).</summary>
    public static string LocalDirectory { get; } = Ensure(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Toolbox"));

    public static string StorePath { get; } =
        Override("TOOLBOX_STORE_PATH") ?? Path.Combine(DataDirectory, "connections.json");

    public static string VaultPath { get; } =
        Override("TOOLBOX_VAULT_PATH") ?? Path.Combine(DataDirectory, "vault.json");

    /// <summary>The DPAPI-protected vault key, next to the vault file.</summary>
    public static string VaultKeyPath { get; } =
        Override("TOOLBOX_VAULT_KEY_PATH")
        ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(VaultPath)) ?? DataDirectory, "vault.key");

    public static string SettingsPath { get; } =
        Override("TOOLBOX_SETTINGS_PATH") ?? Path.Combine(DataDirectory, "settings.json");

    /// <summary>
    /// known_hosts for SSH. Null lets the core use %USERPROFILE%\.ssh\known_hosts,
    /// the file Windows OpenSSH uses.
    /// </summary>
    public static string? KnownHostsPath { get; } = Override("TOOLBOX_KNOWN_HOSTS");

    public static string LogPath { get; } =
        Override("TOOLBOX_LOG_PATH") ?? Path.Combine(Ensure(Path.Combine(LocalDirectory, "logs")), "toolbox.log");

    /// <summary>Profile folder for the terminal's WebView2 (it must be writable; the default is next to the exe).</summary>
    public static string WebViewDataDirectory { get; } =
        Override("TOOLBOX_WEBVIEW_DATA") ?? Path.Combine(LocalDirectory, "WebView2");

    /// <summary>Folder holding the terminal page and xterm.js, shipped next to the executable.</summary>
    public static string TerminalAssets { get; } = Path.Combine(AppContext.BaseDirectory, "Assets", "Terminal");

    private static string? Override(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string Ensure(string directory)
    {
        try { Directory.CreateDirectory(directory); } catch (Exception) { /* reported when a file is written */ }
        return directory;
    }
}
