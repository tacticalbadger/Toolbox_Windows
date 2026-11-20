using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Toolbox.Support;

/// <summary>
/// Glyphs from Segoe Fluent Icons (Windows 11), with Segoe MDL2 Assets as the
/// fallback on Windows 10; both fonts share these code points.
/// </summary>
internal static class Icons
{
    public static readonly FontFamily Font = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public const string Folder = "";
    public const string FolderFilled = "";
    public const string NewFolder = "";
    public const string Terminal = "";
    public const string Desktop = "";
    public const string Display = "";
    public const string FileTransfer = "";
    public const string Lock = "";
    public const string Key = "";
    public const string Shield = "";
    public const string Gear = "";
    public const string Add = "";
    public const string Close = "";
    public const string Delete = "";
    public const string Refresh = "";
    public const string Sync = "";
    public const string Up = "";
    public const string Home = "";
    public const string Upload = "";
    public const string Download = "";
    public const string Document = "";
    public const string Link = "";
    public const string Grip = "";
    public const string Sidebar = "";
    public const string SplitRight = "";
    public const string SplitDown = "";
    public const string Frame = "";
    public const string Info = "";
    public const string Warning = "";
    public const string Error = "";
    public const string More = "";

    /// <summary>A text block showing one glyph.</summary>
    public static TextBlock Glyph(string glyph, double size = 14, Brush? brush = null)
    {
        var block = new TextBlock
        {
            Text = glyph,
            FontFamily = Font,
            FontSize = size,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };
        if (brush is not null) block.Foreground = brush;
        return block;
    }

    /// <summary>Glyph for a connection of this kind.</summary>
    public static string For(ConnectionKind kind) => kind switch
    {
        ConnectionKind.Ssh => Terminal,
        ConnectionKind.Rdp => Desktop,
        ConnectionKind.Vnc => Display,
        _ => FileTransfer,
    };

    /// <summary>Glyph for a credential, by where its secret lives.</summary>
    public static string For(CredentialKind kind) => kind switch
    {
        CredentialKind.Vault => Lock,
        CredentialKind.OnePassword => Shield,
        _ => Key,
    };
}
