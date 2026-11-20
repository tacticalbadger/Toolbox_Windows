using System.Text.Json;

namespace Toolbox.Terminal;

/// <summary>A terminal colour scheme. Colours are 0xRRGGBB.</summary>
internal sealed record TerminalTheme(string Id, string Name, uint Background, uint Foreground, uint Cursor,
                                     uint Selection, uint[] Ansi)
{
    public static readonly TerminalTheme Homebrew = new("homebrew", "Homebrew",
        0x000000, 0x00FF00, 0x23FF18, 0x4D4D4D,
        [0x000000, 0x990000, 0x00A600, 0x999900, 0x0000B2, 0xB200B2, 0x00A6B2, 0xBFBFBF,
         0x666666, 0xE50000, 0x00D900, 0xE5E500, 0x0000FF, 0xE500E5, 0x00E5E5, 0xE5E5E5]);

    public static readonly TerminalTheme OneDark = new("one-dark", "Atom One Dark",
        0x282C34, 0xABB2BF, 0x528BFF, 0x3E4451,
        [0x282C34, 0xE06C75, 0x98C379, 0xE5C07B, 0x61AFEF, 0xC678DD, 0x56B6C2, 0xABB2BF,
         0x5C6370, 0xE06C75, 0x98C379, 0xE5C07B, 0x61AFEF, 0xC678DD, 0x56B6C2, 0xFFFFFF]);

    public static readonly TerminalTheme GruvboxDark = new("gruvbox-dark", "Gruvbox Dark",
        0x282828, 0xEBDBB2, 0xEBDBB2, 0x504945,
        [0x282828, 0xCC241D, 0x98971A, 0xD79921, 0x458588, 0xB16286, 0x689D6A, 0xA89984,
         0x928374, 0xFB4934, 0xB8BB26, 0xFABD2F, 0x83A598, 0xD3869B, 0x8EC07C, 0xEBDBB2]);

    public static readonly TerminalTheme Dracula = new("dracula", "Dracula",
        0x282A36, 0xF8F8F2, 0xF8F8F2, 0x44475A,
        [0x21222C, 0xFF5555, 0x50FA7B, 0xF1FA8C, 0xBD93F9, 0xFF79C6, 0x8BE9FD, 0xF8F8F2,
         0x6272A4, 0xFF6E6E, 0x69FF94, 0xFFFFA5, 0xD6ACFF, 0xFF92DF, 0xA4FFFF, 0xFFFFFF]);

    public static readonly TerminalTheme SolarizedDark = new("solarized-dark", "Solarized Dark",
        0x002B36, 0x839496, 0x93A1A1, 0x073642,
        [0x073642, 0xDC322F, 0x859900, 0xB58900, 0x268BD2, 0xD33682, 0x2AA198, 0xEEE8D5,
         0x002B36, 0xCB4B16, 0x586E75, 0x657B83, 0x839496, 0x6C71C4, 0x93A1A1, 0xFDF6E3]);

    public static readonly TerminalTheme CatppuccinMocha = new("catppuccin-mocha", "Catppuccin Mocha",
        0x1E1E2E, 0xCDD6F4, 0xF5E0DC, 0x45475A,
        [0x45475A, 0xF38BA8, 0xA6E3A1, 0xF9E2AF, 0x89B4FA, 0xF5C2E7, 0x94E2D5, 0xBAC2DE,
         0x585B70, 0xF38BA8, 0xA6E3A1, 0xF9E2AF, 0x89B4FA, 0xF5C2E7, 0x94E2D5, 0xA6ADC8]);

    public static readonly IReadOnlyList<TerminalTheme> All =
        [Homebrew, OneDark, GruvboxDark, Dracula, SolarizedDark, CatppuccinMocha];

    private static readonly string[] AnsiNames =
    [
        "black", "red", "green", "yellow", "blue", "magenta", "cyan", "white",
        "brightBlack", "brightRed", "brightGreen", "brightYellow", "brightBlue", "brightMagenta", "brightCyan", "brightWhite",
    ];

    private static string Hex(uint color) => $"#{color & 0xFFFFFF:x6}";

    /// <summary>The theme as xterm.js takes it.</summary>
    public Dictionary<string, string> ToXterm()
    {
        var theme = new Dictionary<string, string>
        {
            ["background"] = Hex(Background),
            ["foreground"] = Hex(Foreground),
            ["cursor"] = Hex(Cursor),
            ["cursorAccent"] = Hex(Background),
            // 45% opaque, so the text under a selection stays readable.
            ["selectionBackground"] = Hex(Selection) + "73",
        };
        for (var i = 0; i < AnsiNames.Length; i++) theme[AnsiNames[i]] = Hex(Ansi[i]);
        return theme;
    }
}

/// <summary>The active theme and font. Changes are broadcast to every open terminal.</summary>
internal sealed class ThemeStore
{
    public static ThemeStore Shared { get; } = new();

    public const string DefaultFontFamily = "\"Cascadia Mono\", Consolas, \"Courier New\", monospace";

    private readonly AppSettings settings = AppSettings.Shared;
    private TerminalTheme current;

    public event Action? Changed;

    private ThemeStore()
    {
        current = TerminalTheme.All.FirstOrDefault(t => t.Id == settings.Theme) ?? TerminalTheme.Homebrew;
    }

    public TerminalTheme Current
    {
        get => current;
        set
        {
            current = value;
            settings.Theme = value.Id;
            settings.SaveSoon();
            Changed?.Invoke();
        }
    }

    public double FontSize
    {
        get => settings.FontSize;
        set
        {
            settings.FontSize = Math.Clamp(value, AppSettings.MinFontSize, AppSettings.MaxFontSize);
            settings.SaveSoon();
            Changed?.Invoke();
        }
    }

    /// <summary>CSS font-family for the terminal: the chosen font first, then the defaults.</summary>
    public string FontFamily =>
        string.IsNullOrWhiteSpace(settings.FontName)
            ? DefaultFontFamily
            : $"\"{settings.FontName.Replace("\"", "")}\", {DefaultFontFamily}";

    /// <summary>The message that tells a terminal page how to look.</summary>
    public string ToMessage() => JsonSerializer.Serialize(new
    {
        t = "theme",
        fontSize = FontSize,
        fontFamily = FontFamily,
        theme = current.ToXterm(),
    });
}
