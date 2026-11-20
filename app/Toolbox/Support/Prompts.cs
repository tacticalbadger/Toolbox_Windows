using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Toolbox.Support;

/// <summary>Small app-modal prompts. All must be called on the UI thread.</summary>
internal static class Prompts
{
    private enum Tone { Question, Info, Warning, Error }

    public static bool Confirm(string title, string message, string ok, bool cancelIsDefault = false) =>
        Show(title, message, Tone.Warning, ok, cancel: "Cancel", cancelIsDefault: cancelIsDefault).Accepted;

    public static void Info(string title, string message) =>
        Show(title, message, Tone.Info, "OK", cancel: null);

    public static void Error(string title, string message) =>
        Show(title, message, Tone.Error, "OK", cancel: null);

    public static string? AskText(string title, string message, string initial, string ok = "OK")
    {
        var box = new TextBox { Text = initial, MinWidth = 320 };
        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        var result = Show(title, message, Tone.Question, ok, cancel: "Cancel", input: box);
        return result.Accepted ? box.Text : null;
    }

    public static string? AskSecret(string title, string message, string ok = "Connect")
    {
        var box = new PasswordBox { MinWidth = 320 };
        box.Loaded += (_, _) => box.Focus();
        var result = Show(title, message, Tone.Question, ok, cancel: "Cancel", input: box);
        return result.Accepted ? box.Password : null;
    }

    /// <summary>
    /// Asks before trusting an SSH host key seen for the first time. Cancel is
    /// the default button: trusting a host should take a deliberate click.
    /// </summary>
    public static bool ConfirmHostKey(string host, ushort port, string keyType, string fingerprint)
    {
        var message =
            "The authenticity of this host can't be established yet.\n\n" +
            $"{keyType} key fingerprint:\n{fingerprint}\n\n" +
            "Trust it to add it to your known_hosts file.";
        return Show($"Unknown host key for {host}:{port}", message, Tone.Warning, "Trust",
                    cancel: "Cancel", cancelIsDefault: true, selectable: true).Accepted;
    }

    /// <summary>The window a prompt (or a file dialog) should be modal to.</summary>
    public static Window? Owner()
    {
        var app = Application.Current;
        if (app is null) return null;
        // The active window is the main one, or an editor already open on top of it.
        foreach (Window window in app.Windows)
        {
            if (window.IsVisible && window.IsActive) return window;
        }
        return app.MainWindow is { IsVisible: true } main ? main : null;
    }

    private readonly record struct Result(bool Accepted);

    private static Result Show(string title, string message, Tone tone, string ok, string? cancel,
                               bool cancelIsDefault = false, UIElement? input = null, bool selectable = false)
    {
        var owner = Owner();
        var window = new Window
        {
            Title = "Toolbox",
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = owner is null,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner,
            MinWidth = 380,
            MaxWidth = 560,
        };
        AutomationProperties.SetAutomationId(window, "Prompt");

        var (glyph, color) = tone switch
        {
            Tone.Info => (Icons.Info, Color.FromRgb(0x2B, 0x7B, 0xE4)),
            Tone.Warning => (Icons.Warning, Color.FromRgb(0xD9, 0x8A, 0x00)),
            Tone.Error => (Icons.Error, Color.FromRgb(0xD1, 0x34, 0x38)),
            _ => (Icons.Info, Color.FromRgb(0x2B, 0x7B, 0xE4)),
        };

        var heading = new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetAutomationId(heading, "PromptTitle");

        FrameworkElement body;
        if (selectable)
        {
            // Fingerprints are worth being able to copy and compare.
            body = new TextBox
            {
                Text = message,
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                TextWrapping = TextWrapping.Wrap,
                Padding = new Thickness(0),
                IsTabStop = false,
            };
        }
        else
        {
            body = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
        }
        body.Margin = new Thickness(0, 8, 0, 0);
        body.Opacity = 0.85;
        AutomationProperties.SetAutomationId(body, "PromptMessage");

        var text = new StackPanel { Margin = new Thickness(14, 0, 0, 0) };
        text.Children.Add(heading);
        if (!string.IsNullOrEmpty(message)) text.Children.Add(body);
        if (input is FrameworkElement field)
        {
            field.Margin = new Thickness(0, 12, 0, 0);
            AutomationProperties.SetAutomationId(field, "PromptInput");
            text.Children.Add(field);
        }

        var top = new DockPanel { LastChildFill = true };
        var icon = Icons.Glyph(glyph, 26, new SolidColorBrush(color));
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 2, 0, 0);
        DockPanel.SetDock(icon, Dock.Left);
        top.Children.Add(icon);
        top.Children.Add(text);

        var accepted = false;
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0),
        };
        var okButton = new Button { Content = ok, MinWidth = 88, Padding = new Thickness(14, 5, 14, 5) };
        AutomationProperties.SetAutomationId(okButton, "PromptOk");
        okButton.Click += (_, _) => { accepted = true; window.Close(); };
        buttons.Children.Add(okButton);
        if (cancel is not null)
        {
            var cancelButton = new Button
            {
                Content = cancel,
                MinWidth = 88,
                Padding = new Thickness(14, 5, 14, 5),
                Margin = new Thickness(8, 0, 0, 0),
                IsCancel = true,
                IsDefault = cancelIsDefault,
            };
            AutomationProperties.SetAutomationId(cancelButton, "PromptCancel");
            buttons.Children.Add(cancelButton);
            if (cancelIsDefault) cancelButton.Loaded += (_, _) => cancelButton.Focus();
        }
        else
        {
            okButton.IsCancel = true;
        }
        if (!cancelIsDefault)
        {
            okButton.IsDefault = true;
            okButton.SetResourceReference(FrameworkElement.StyleProperty, "AccentButtonStyle");
            if (input is null) okButton.Loaded += (_, _) => okButton.Focus();
        }

        var root = new StackPanel { Margin = new Thickness(22, 20, 22, 18) };
        root.Children.Add(top);
        root.Children.Add(buttons);
        window.Content = root;

        Log.Info($"prompt: {title}");
        window.ShowDialog();
        Log.Info($"prompt answered: {title} -> {(accepted ? ok : cancel ?? "closed")}");
        return new Result(accepted);
    }
}
