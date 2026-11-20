using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Toolbox.Workspaces;

/// <summary>
/// One cell of the workspace layout: a header strip plus the session view,
/// or a placeholder when empty. Panes are dragged by their header and accept
/// drops of other panes and of sidebar connections.
/// </summary>
internal sealed class PaneView : Border
{
    public Guid Id { get; }

    /// <summary>The session shown here, or null for an empty pane.</summary>
    public IPaneContent? Content { get; private set; }

    public event Action? FocusRequested;
    public event Action? CloseRequested;

    /// <summary>The header was dragged far enough to mean "move this pane".</summary>
    public event Action<MouseEventArgs>? DragRequested;

    /// <summary>Context menu for the header (move to workspace, close).</summary>
    public Func<ContextMenu?>? ContextMenuProvider { get; set; }

    private readonly Grid header = new();
    private readonly Ellipse statusDot = new() { Width = 8, Height = 8 };
    private readonly TextBlock titleLabel = new();
    private readonly Grid host = new();
    private readonly TextBlock placeholder = new();
    private bool isActive;
    private bool showsFocusRing;
    private Point? pressedAt;

    public PaneView(Guid id)
    {
        Id = id;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(4);
        Background = Brushes.Transparent; // an empty pane still takes clicks and drops
        Focusable = true;
        FocusVisualStyle = null;
        SnapsToDevicePixels = true;
        AutomationProperties.SetAutomationId(this, "Pane");

        BuildHeader();
        BuildPlaceholder();

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(header, 0);
        Grid.SetRow(host, 1);
        layout.Children.Add(header);
        layout.Children.Add(host);
        host.Children.Add(placeholder);
        Child = layout;

        MouseDown += (_, _) =>
        {
            if (!isActive) FocusRequested?.Invoke();
            if (Content is null) Focus();
        };
        UpdateBorder();
    }

    /// <summary>Whether this is the workspace's active pane.</summary>
    public bool IsActivePane
    {
        get => isActive;
        set
        {
            isActive = value;
            UpdateBorder();
        }
    }

    /// <summary>Whether to draw the focus border at all (pointless with a single pane).</summary>
    public bool ShowsFocusRing
    {
        get => showsFocusRing;
        set
        {
            showsFocusRing = value;
            UpdateBorder();
        }
    }

    // ----- Content -----

    /// <summary>Replaces the session, ending the one that was here.</summary>
    public void SetContent(IPaneContent? newContent)
    {
        var old = Content;
        if (ReferenceEquals(old, newContent)) return;
        Attach(newContent);
        old?.Terminate();
    }

    /// <summary>Detaches the session without ending it, for moving it to another pane.</summary>
    public IPaneContent? TakeContent()
    {
        var current = Content;
        Attach(null);
        return current;
    }

    private void Attach(IPaneContent? newContent)
    {
        if (Content is { } old)
        {
            old.TitleChanged -= OnTitleChanged;
            old.StateChanged -= RefreshStatus;
            old.FocusRequested -= OnContentFocus;
            old.SessionEnded -= OnSessionEnded;
            host.Children.Remove(old.View);
        }
        Content = newContent;

        if (newContent is null)
        {
            header.Visibility = Visibility.Collapsed;
            placeholder.Visibility = Visibility.Visible;
            titleLabel.Text = "";
            return;
        }

        header.Visibility = Visibility.Visible;
        placeholder.Visibility = Visibility.Collapsed;
        titleLabel.Text = newContent.Title;
        newContent.TitleChanged += OnTitleChanged;
        newContent.StateChanged += RefreshStatus;
        newContent.FocusRequested += OnContentFocus;
        newContent.SessionEnded += OnSessionEnded;
        host.Children.Add(newContent.View);
        RefreshStatus();
    }

    private void OnTitleChanged()
    {
        if (Content is not null) titleLabel.Text = Content.Title;
    }

    private void OnContentFocus()
    {
        if (!isActive) FocusRequested?.Invoke();
    }

    /// <summary>
    /// A clean end (sign-out, logout) takes the pane with it after a moment;
    /// a failure stays visible so the message can be read.
    /// </summary>
    private void OnSessionEnded(bool clean)
    {
        if (!clean) return;
        var ended = Content;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (ended is not null && ReferenceEquals(Content, ended)) CloseRequested?.Invoke();
        };
        timer.Start();
    }

    /// <summary>Gives the pane's session (or the empty pane itself) the keyboard.</summary>
    public void FocusSession()
    {
        if (Content is not null) Content.FocusSession();
        else Focus();
    }

    private void RefreshStatus()
    {
        var live = Content?.IsLive ?? false;
        statusDot.SetResourceReference(Shape.FillProperty, live ? "SystemFillColorSuccessBrush" : "TextFillColorTertiaryBrush");
        AutomationProperties.SetHelpText(this, live ? "live" : "idle");
        var cameUp = live && !wasLive;
        wasLive = live;
        // A session that has just come up in the active pane takes the
        // keyboard, as if it had been there when the pane was chosen (a remote
        // desktop has nothing to focus until it connects). Not while
        // something is being typed elsewhere in the window.
        if (!cameUp) return;
        var session = Content;
        // Once the window has caught up: a remote desktop's control is only
        // there to take the keyboard after it has been shown.
        Dispatcher.BeginInvoke(() =>
        {
            if (session is { IsLive: true } && ReferenceEquals(Content, session) && isActive &&
                Window.GetWindow(this) is { IsActive: true } &&
                Keyboard.FocusedElement is not (System.Windows.Controls.Primitives.TextBoxBase or PasswordBox))
            {
                session.FocusSession();
            }
        }, DispatcherPriority.Input);
    }

    private bool wasLive;

    private void UpdateBorder()
    {
        SetResourceReference(BorderBrushProperty,
            isActive && showsFocusRing ? "AccentFillColorDefaultBrush" : "ControlStrokeColorDefaultBrush");
    }

    // ----- Building -----

    private void BuildHeader()
    {
        header.Height = 26;
        header.Visibility = Visibility.Collapsed;
        header.ToolTip = "Drag to rearrange";
        header.Cursor = Cursors.SizeAll;
        header.SetResourceReference(Panel.BackgroundProperty, "ControlFillColorDefaultBrush");
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        statusDot.Margin = new Thickness(9, 0, 6, 0);
        statusDot.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(statusDot, 0);

        titleLabel.FontSize = 12;
        titleLabel.FontWeight = FontWeights.Medium;
        titleLabel.TextTrimming = TextTrimming.CharacterEllipsis;
        titleLabel.VerticalAlignment = VerticalAlignment.Center;
        titleLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        AutomationProperties.SetAutomationId(titleLabel, "PaneTitle");
        Grid.SetColumn(titleLabel, 1);

        var grip = Icons.Glyph(Icons.Grip, 12);
        grip.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        grip.IsHitTestVisible = false;
        Grid.SetColumnSpan(grip, 3);

        var close = new Button
        {
            Content = Icons.Glyph(Icons.Close, 10),
            Width = 24,
            Height = 20,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 0, 4, 0),
            ToolTip = "Close pane",
            Focusable = false,
            Cursor = Cursors.Arrow,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
        };
        AutomationProperties.SetAutomationId(close, "PaneClose");
        AutomationProperties.SetName(close, "Close pane");
        close.Click += (_, _) => CloseRequested?.Invoke();
        Grid.SetColumn(close, 2);

        header.Children.Add(statusDot);
        header.Children.Add(titleLabel);
        header.Children.Add(grip);
        header.Children.Add(close);

        header.MouseLeftButtonDown += (_, e) =>
        {
            pressedAt = e.GetPosition(header);
            if (!isActive) FocusRequested?.Invoke();
            // Keep receiving the mouse once it leaves this thin strip: a quick
            // drag is out of the header before it has moved far enough to count.
            if (Content is not null) header.CaptureMouse();
        };
        header.MouseMove += (_, e) =>
        {
            if (pressedAt is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
            var now = e.GetPosition(header);
            if (Math.Abs(now.X - start.X) > 4 || Math.Abs(now.Y - start.Y) > 4)
            {
                pressedAt = null;
                header.ReleaseMouseCapture();
                if (Content is not null) DragRequested?.Invoke(e);
            }
        };
        header.MouseLeftButtonUp += (_, _) =>
        {
            pressedAt = null;
            header.ReleaseMouseCapture();
        };
        header.MouseRightButtonUp += (_, e) =>
        {
            if (!isActive) FocusRequested?.Invoke();
            var menu = ContextMenuProvider?.Invoke();
            if (menu is null) return;
            menu.PlacementTarget = header;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.IsOpen = true;
            e.Handled = true;
        };
    }

    private void BuildPlaceholder()
    {
        placeholder.Text = "No session\n\nDouble-click a connection in the sidebar, drag one here,\nor press Ctrl+Shift+N to create one.";
        placeholder.TextAlignment = TextAlignment.Center;
        placeholder.TextWrapping = TextWrapping.Wrap;
        placeholder.FontSize = 13;
        placeholder.HorizontalAlignment = HorizontalAlignment.Center;
        placeholder.VerticalAlignment = VerticalAlignment.Center;
        placeholder.Margin = new Thickness(20);
        placeholder.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
    }
}
