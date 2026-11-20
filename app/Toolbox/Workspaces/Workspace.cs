using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Toolbox.Workspaces;

/// <summary>
/// One workspace: a named pane layout, shown as a tab. Any mix of sessions
/// can live in any of them.
/// </summary>
internal sealed class Workspace
{
    private readonly TextBlock label = new();
    private readonly Border indicator = new();
    private string name;

    public Workspace(string name)
    {
        this.name = name;
        Panes = new PaneContainer();
        Tab = BuildTab();
        IsSelected = false;
    }

    public PaneContainer Panes { get; }

    /// <summary>The tab that stands for this workspace in the tab row.</summary>
    public Border Tab { get; }

    public event Action? Selected;
    public event Action? CloseRequested;
    public event Action? RenameRequested;

    public string Name
    {
        get => name;
        set
        {
            name = value;
            label.Text = value;
            AutomationProperties.SetName(Tab, value);
        }
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
        label.Text = name;
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
        AutomationProperties.SetName(tab, name);
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
