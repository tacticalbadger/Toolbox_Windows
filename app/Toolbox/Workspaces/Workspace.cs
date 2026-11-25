using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Toolbox.Workspaces;

/// <summary>
/// One workspace: a pane layout, shown as a tab. A session opens in a
/// workspace of its own; any mix of sessions can be brought together in one.
/// </summary>
internal sealed class Workspace
{
    private readonly TextBlock label = new();
    private readonly Border indicator = new();
    /// <summary>What the workspace is called while nothing is in it ("Workspace 3").</summary>
    private readonly string placeholderName;
    private string? customName;
    private string title;

    public Workspace(string name)
    {
        placeholderName = name;
        title = name;
        Panes = new PaneContainer();
        Tab = BuildTab();
        IsSelected = false;
        Panes.ContentChanged += RefreshTitle;
        Panes.LayoutChanged += RefreshTitle;
    }

    public PaneContainer Panes { get; }

    /// <summary>The tab that stands for this workspace in the tab row.</summary>
    public Border Tab { get; }

    public event Action? Selected;
    public event Action? CloseRequested;
    public event Action? RenameRequested;
    public event Action? TitleChanged;

    /// <summary>
    /// The tab's name: the user's, else the sessions in it, else the
    /// placeholder.
    /// </summary>
    public string Title => title;

    /// <summary>
    /// The name the user gave the workspace. Without one (null) its tab is
    /// named after the sessions in it.
    /// </summary>
    public string? CustomName
    {
        get => customName;
        set
        {
            customName = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            RefreshTitle();
        }
    }

    private void RefreshTitle()
    {
        var wanted = customName ?? TitleFor(Panes.Contents.Select(c => c.TabTitle).ToList()) ?? placeholderName;
        if (wanted == title) return;
        title = wanted;
        label.Text = wanted;
        AutomationProperties.SetName(Tab, wanted);
        TitleChanged?.Invoke();
    }

    /// <summary>"web01", "web01, db02", "web01, db02 + 3"; null for no sessions.</summary>
    internal static string? TitleFor(IReadOnlyList<string> sessions)
    {
        if (sessions.Count == 0) return null;
        var shown = string.Join(", ", sessions.Take(2));
        return sessions.Count > 2 ? $"{shown} + {sessions.Count - 2}" : shown;
    }

    public bool IsSelected
    {
        set
        {
            Tab.SetResourceReference(Border.BackgroundProperty,
                                     value ? "ControlFillColorSecondaryBrush" : "SubtleFillColorTransparentBrush");
            indicator.Visibility = value ? Visibility.Visible : Visibility.Hidden;
            label.SetResourceReference(TextBlock.ForegroundProperty,
                                       value ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
            AutomationProperties.SetItemStatus(Tab, value ? "selected" : "");
        }
    }

    private Border BuildTab()
    {
        label.Text = title;
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(12, 0, 6, 0);
        label.MaxWidth = 200;
        label.TextTrimming = TextTrimming.CharacterEllipsis;

        var close = new Button
        {
            Content = Icons.Glyph(Icons.Close, 8),
            Width = 20,
            Height = 20,
            ToolTip = "Close workspace",
            VerticalAlignment = VerticalAlignment.Center,
        };
        close.SetResourceReference(FrameworkElement.StyleProperty, "ToolButton");
        AutomationProperties.SetAutomationId(close, "WorkspaceClose");
        AutomationProperties.SetName(close, "Close workspace");
        close.Click += (_, _) => CloseRequested?.Invoke();

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(label);
        row.Children.Add(close);

        indicator.Height = 2;
        indicator.CornerRadius = new CornerRadius(1);
        indicator.Margin = new Thickness(10, 0, 10, 0);
        indicator.VerticalAlignment = VerticalAlignment.Bottom;
        indicator.SetResourceReference(Border.BackgroundProperty, "AccentFillColorDefaultBrush");

        var content = new Grid();
        content.Children.Add(row);
        content.Children.Add(indicator);

        var tab = new Border
        {
            CornerRadius = new CornerRadius(6, 6, 0, 0),
            Padding = new Thickness(0, 0, 6, 0),
            Margin = new Thickness(0, 4, 2, 0),
            Height = 32,
            Child = content,
            ToolTip = "Double-click to rename",
        };
        AutomationProperties.SetAutomationId(tab, "WorkspaceTab");
        AutomationProperties.SetName(tab, title);
        tab.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) RenameRequested?.Invoke();
            else Selected?.Invoke();
        };
        tab.MouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle) CloseRequested?.Invoke();
        };

        var menu = new ContextMenu();
        var rename = new MenuItem { Header = "Rename…" };
        rename.Click += (_, _) => RenameRequested?.Invoke();
        var closeItem = new MenuItem { Header = "Close Workspace" };
        closeItem.Click += (_, _) => CloseRequested?.Invoke();
        menu.Items.Add(rename);
        menu.Items.Add(closeItem);
        tab.ContextMenu = menu;
        return tab;
    }
}

/// <summary>A command that just runs a delegate, for keyboard shortcuts.</summary>
internal sealed class RelayCommand(Action run, Func<bool>? canRun = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canRun?.Invoke() ?? true;

    public void Execute(object? parameter) => run();
}
