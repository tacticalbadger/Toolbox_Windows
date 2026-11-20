using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace Toolbox.Sidebar;

/// <summary>
/// Builds the grouped forms of the editors: titled sections holding
/// label/field rows and small notes, all bound to the editor's draft (the
/// data context). "visibleWhen" names a bool property of the draft.
/// </summary>
internal sealed class FormBuilder
{
    private static readonly BooleanToVisibilityConverter BoolToVisibility = new();
    private const double LabelWidth = 150;

    public StackPanel Root { get; } = new() { Margin = new Thickness(20, 6, 20, 8) };
    private StackPanel rows = new();

    /// <summary>Starts a section; the rows that follow go into it.</summary>
    public StackPanel Section(string title, string? visibleWhen = null, object? dataContext = null)
    {
        var heading = new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(2, 12, 0, 6),
        };
        rows = new StackPanel();
        var card = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 8, 14, 10),
            Child = rows,
        };
        card.SetResourceReference(Border.BackgroundProperty, "ControlFillColorDefaultBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        var section = new StackPanel();
        section.Children.Add(heading);
        section.Children.Add(card);
        if (dataContext is not null) section.DataContext = dataContext;
        ShowWhen(section, visibleWhen);
        Root.Children.Add(section);
        return section;
    }

    /// <summary>A label with a field next to it.</summary>
    public FrameworkElement Row(string label, FrameworkElement field, string? visibleWhen = null)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 10, 0),
        };
        Grid.SetColumn(field, 1);
        grid.Children.Add(text);
        grid.Children.Add(field);
        if (AutomationProperties.GetName(field).Length == 0) AutomationProperties.SetName(field, label);
        ShowWhen(grid, visibleWhen);
        rows.Children.Add(grid);
        return grid;
    }

    /// <summary>An element spanning the section's width.</summary>
    public T Wide<T>(T element, string? visibleWhen = null) where T : FrameworkElement
    {
        element.Margin = new Thickness(0, 4, 0, 4);
        ShowWhen(element, visibleWhen);
        rows.Children.Add(element);
        return element;
    }

    /// <summary>Small explanatory text. <paramref name="textPath"/> binds the text to a draft property instead.</summary>
    public TextBlock Note(string? text, string? visibleWhen = null, bool warning = false, string? textPath = null)
    {
        var note = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 3),
        };
        if (textPath is not null) note.SetBinding(TextBlock.TextProperty, new Binding(textPath) { Mode = BindingMode.OneWay });
        else note.Text = text ?? "";
        note.SetResourceReference(TextBlock.ForegroundProperty,
                                  warning ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush");
        ShowWhen(note, visibleWhen);
        rows.Children.Add(note);
        return note;
    }

    // ----- Fields -----

    /// <summary>A text box bound two-way to a string property, with greyed hint text while empty.</summary>
    public static FrameworkElement Text(string path, string? hint = null, string? hintPath = null, string? automationId = null)
    {
        var box = new TextBox();
        box.SetBinding(TextBox.TextProperty, new Binding(path)
        {
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
        });
        AutomationProperties.SetAutomationId(box, automationId ?? path);
        return WithHint(box, hint, hintPath);
    }

    /// <summary>Lays greyed hint text over a text box while it is empty.</summary>
    public static FrameworkElement WithHint(TextBox box, string? hint, string? hintPath = null)
    {
        if (hint is null && hintPath is null) return box;
        var overlay = new TextBlock
        {
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(11, 0, 8, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        if (hintPath is not null) overlay.SetBinding(TextBlock.TextProperty, new Binding(hintPath) { Mode = BindingMode.OneWay });
        else overlay.Text = hint!;
        overlay.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        overlay.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(TextBox.Text))
        {
            Source = box,
            Converter = EmptyToVisible.Instance,
        });
        var grid = new Grid();
        grid.Children.Add(box);
        grid.Children.Add(overlay);
        return grid;
    }

    /// <summary>A password box; <paramref name="changed"/> receives what is typed.</summary>
    public static PasswordBox Secret(Action<string> changed, string automationId)
    {
        var box = new PasswordBox();
        box.PasswordChanged += (_, _) => changed(box.Password);
        AutomationProperties.SetAutomationId(box, automationId);
        return box;
    }

    /// <summary>A drop-down over a list property of the draft, with the selected entry bound two-way.</summary>
    public static ComboBox Choice(string itemsPath, string selectedPath, string? automationId = null)
    {
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        combo.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(itemsPath) { Mode = BindingMode.OneWay });
        combo.SetBinding(Selector.SelectedItemProperty, new Binding(selectedPath) { Mode = BindingMode.TwoWay });
        AutomationProperties.SetAutomationId(combo, automationId ?? selectedPath);
        return combo;
    }

    public static CheckBox Toggle(string label, string path)
    {
        var box = new CheckBox { Content = label };
        box.SetBinding(ToggleButton.IsCheckedProperty, new Binding(path) { Mode = BindingMode.TwoWay });
        AutomationProperties.SetAutomationId(box, path);
        return box;
    }

    public static void ShowWhen(FrameworkElement element, string? path)
    {
        if (path is null) return;
        element.SetBinding(UIElement.VisibilityProperty, new Binding(path)
        {
            Mode = BindingMode.OneWay,
            Converter = BoolToVisibility,
        });
    }

    private sealed class EmptyToVisible : IValueConverter
    {
        public static readonly EmptyToVisible Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            string.IsNullOrEmpty(value as string) ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            Binding.DoNothing;
    }
}

/// <summary>Shared chrome of the editor dialogs: scrolling form, validation line, button row.</summary>
internal abstract class EditorWindow : Window
{
    private readonly TextBlock validation = new()
    {
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(22, 0, 22, 0),
        Visibility = Visibility.Collapsed,
    };

    private readonly StackPanel buttons = new()
    {
        Orientation = Orientation.Horizontal,
        HorizontalAlignment = HorizontalAlignment.Right,
        Margin = new Thickness(20, 12, 20, 16),
    };

    protected EditorWindow(string title, double width, double maxHeight)
    {
        Title = title;
        Width = width;
        MaxHeight = maxHeight;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        validation.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
        AutomationProperties.SetAutomationId(validation, "Validation");
    }

    protected void SetForm(FormBuilder form)
    {
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = form.Root,
            Focusable = false,
        };
        var layout = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(validation, Dock.Bottom);
        layout.Children.Add(buttons);
        layout.Children.Add(validation);
        layout.Children.Add(scroll);
        Content = layout;
    }

    protected Button AddButton(string label, Action clicked, bool isDefault = false, bool isCancel = false, string? automationId = null)
    {
        var button = new Button
        {
            Content = label,
            MinWidth = 88,
            Padding = new Thickness(14, 5, 14, 5),
            Margin = new Thickness(8, 0, 0, 0),
            IsDefault = isDefault,
            IsCancel = isCancel,
        };
        if (isDefault) button.SetResourceReference(StyleProperty, "AccentButtonStyle");
        AutomationProperties.SetAutomationId(button, automationId ?? "Editor" + label.Replace(" ", ""));
        button.Click += (_, _) => clicked();
        buttons.Children.Add(button);
        return button;
    }

    protected void ShowValidation(string? message)
    {
        validation.Text = message ?? "";
        validation.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Disables the buttons while a save is running, so it cannot be started twice.</summary>
    protected void SetBusy(bool busy) => buttons.IsEnabled = !busy;

    private bool closed;

    protected override void OnClosed(EventArgs e)
    {
        closed = true;
        base.OnClosed(e);
    }

    /// <summary>Closes the dialog as accepted, unless it was closed while a save was still running.</summary>
    protected void Accept()
    {
        if (!closed) DialogResult = true;
    }
}
