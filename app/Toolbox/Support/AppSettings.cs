using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;

namespace Toolbox.Support;

/// <summary>
/// Preferences that are not connections: terminal theme and text size, the
/// vault's "require authentication" switch, the Remote Desktop switches,
/// sidebar expansion and the window position. Stored as settings.json next
/// to the connections.
/// </summary>
internal sealed class AppSettings
{
    // Declared before Shared on purpose: static fields are initialised in
    // file order, and Load() needs these.
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static AppSettings Shared { get; } = Load();

    public string Theme { get; set; } = "homebrew";
    public double FontSize { get; set; } = DefaultFontSize;
    public string? FontName { get; set; }

    /// <summary>Ask Windows to confirm it is you before the vault unlocks.</summary>
    public bool VaultRequiresAuth { get; set; } = true;

    /// <summary>Ids of the sidebar nodes that are expanded. Null until the tree has been shown once.</summary>
    public List<string>? ExpandedNodes { get; set; }

    // View > Remote Desktop: switches for every RDP connection, both on by default.

    /// <summary>
    /// Let the Remote Desktop control decode and draw with the graphics card.
    /// Off is the software path, for a PC whose picture comes out garbled or
    /// black. Applies when connecting.
    /// </summary>
    public bool RdpFastGraphics { get; set; } = true;

    /// <summary>
    /// Share the clipboard with remote desktops. Whether a session has the
    /// clipboard at all is decided when it connects; switching this off also
    /// stops telling sessions that are already open about anything new.
    /// </summary>
    public bool RdpShareClipboard { get; set; } = true;

    /// <summary>Look for a new version once a day (Help > Check for Updates Automatically).</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>When the update feed was last read successfully.</summary>
    public DateTimeOffset? LastUpdateCheck { get; set; }

    public bool SidebarVisible { get; set; } = true;
    public double SidebarWidth { get; set; } = 290;
    public WindowPlacement? Window { get; set; }

    public const double DefaultFontSize = 14;
    public const double MinFontSize = 8;
    public const double MaxFontSize = 40;

    private DispatcherTimer? saveTimer;

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsPath), Options);
                if (loaded is not null)
                {
                    loaded.FontSize = Math.Clamp(loaded.FontSize, MinFontSize, MaxFontSize);
                    return loaded;
                }
            }
        }
        catch (Exception e)
        {
            Log.Warn($"settings.json could not be read, using defaults: {e.Message}");
        }
        return new AppSettings();
    }

    /// <summary>Writes soon; several changes in a row cost one write.</summary>
    public void SaveSoon()
    {
        if (saveTimer is null)
        {
            saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            saveTimer.Tick += (_, _) =>
            {
                saveTimer.Stop();
                Save();
            };
        }
        saveTimer.Stop();
        saveTimer.Start();
    }

    public void Save()
    {
        saveTimer?.Stop();
        try
        {
            var path = AppPaths.SettingsPath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            // Sibling temp file then replace, so a crash never leaves half a file.
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Options));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e)
        {
            Log.Warn($"settings.json could not be written: {e.Message}");
        }
    }
}

internal sealed class WindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}
