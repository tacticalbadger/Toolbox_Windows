using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Toolbox.Support;

/// <summary>What the user sees of an update: the offer with its release notes, and the download's progress.</summary>
internal static class UpdateWindow
{
    /// <summary>Offers a version. True when the user chose to install it.</summary>
    public static bool Offer(UpdateFeed feed)
    {
        var owner = Prompts.Owner();
        var window = new Window
        {
            Title = "Toolbox",
            Width = 560,
            SizeToContent = SizeToContent.Height,
            MaxHeight = 640,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = owner is null,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner,
        };
        AutomationProperties.SetAutomationId(window, "UpdateOffer");

        var heading = new TextBlock
        {
            Text = "A new version of Toolbox is available",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetAutomationId(heading, "UpdateTitle");
        var summary = new TextBlock
        {
            Text = $"Toolbox {feed.Version} is out; you have {AppInfo.Version}. Installing restarts Toolbox.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            Opacity = 0.85,
        };
        AutomationProperties.SetAutomationId(summary, "UpdateSummary");
        // The notes are the release's own text; shown as text, never interpreted.
        var notes = new TextBox
        {
            Text = feed.Notes.Length == 0 ? "No notes came with this version." : feed.Notes,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 340,
            MinHeight = 90,
            Margin = new Thickness(0, 14, 0, 0),
            Padding = new Thickness(8),
            IsTabStop = false,
        };
        AutomationProperties.SetAutomationId(notes, "UpdateNotes");

        var accepted = false;
        var install = new Button { Content = "Install and Restart", MinWidth = 130, Padding = new Thickness(14, 5, 14, 5), IsDefault = true };
        install.SetResourceReference(FrameworkElement.StyleProperty, "AccentButtonStyle");
        AutomationProperties.SetAutomationId(install, "UpdateInstall");
        install.Click += (_, _) => { accepted = true; window.Close(); };
        var later = new Button { Content = "Later", MinWidth = 88, Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        AutomationProperties.SetAutomationId(later, "UpdateLater");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(install);
        buttons.Children.Add(later);

        var root = new StackPanel { Margin = new Thickness(22, 20, 22, 18) };
        root.Children.Add(heading);
        root.Children.Add(summary);
        root.Children.Add(notes);
        root.Children.Add(buttons);
        window.Content = root;

        Log.Info($"prompt: update {feed.Version} offered");
        window.ShowDialog();
        Log.Info($"prompt answered: update {feed.Version} -> {(accepted ? "Install and Restart" : "Later")}");
        return accepted;
    }

    /// <summary>A small window that follows a download. Cancel stops it.</summary>
    internal sealed class Progress
    {
        private readonly Window window;
        private readonly ProgressBar bar;
        private bool closed;

        public Progress(UpdateFeed feed, CancellationTokenSource cancel)
        {
            var owner = Prompts.Owner();
            bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6, Margin = new Thickness(0, 14, 0, 0) };
            var label = new TextBlock { Text = $"Downloading Toolbox {feed.Version}…", FontSize = 14 };
            var stop = new Button { Content = "Cancel", MinWidth = 88, Padding = new Thickness(14, 5, 14, 5), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0), IsCancel = true };
            stop.Click += (_, _) => cancel.Cancel();
            var root = new StackPanel { Margin = new Thickness(22, 20, 22, 18) };
            root.Children.Add(label);
            root.Children.Add(bar);
            root.Children.Add(stop);
            window = new Window
            {
                Title = "Toolbox",
                Width = 420,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
                Owner = owner,
                Content = root,
            };
            AutomationProperties.SetAutomationId(window, "UpdateProgress");
            // Closing the window by its own button is a cancel too.
            window.Closing += (_, _) => { if (!closed) cancel.Cancel(); };
            window.Show();
        }

        public void Report(double done) => bar.Value = Math.Clamp(done, 0, 1);

        public void Close()
        {
            if (closed) return;
            closed = true;
            window.Close();
        }
    }

    public static Progress ShowProgress(UpdateFeed feed, CancellationTokenSource cancel) => new(feed, cancel);
}
