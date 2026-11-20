using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Toolbox.Sidebar;

/// <summary>
/// Chooses a credential from a searchable list, grouped by where the secret
/// lives. A 1Password provider can contribute hundreds of items, so typing
/// narrows the list to the names, usernames and folders that match.
/// </summary>
internal static class CredentialPicker
{
    /// <summary>
    /// "Name (username)", or whichever of the two exists, so items without a
    /// title still read as something.
    /// </summary>
    public static string Label(Credential credential)
    {
        var name = credential.Name.Trim();
        var user = credential.Username.Trim();
        return (name.Length == 0, user.Length == 0) switch
        {
            (false, false) => name == user ? name : $"{name} ({user})",
            (false, true) => name,
            (true, false) => user,
            _ => "Untitled credential",
        };
    }

    /// <summary>
    /// The credentials whose name, username or folder contain every word of
    /// <paramref name="query"/>, ignoring case. An empty query matches everything.
    /// </summary>
    public static IReadOnlyList<Credential> Matches(IReadOnlyList<Credential> credentials, string query)
    {
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return credentials;
        return credentials.Where(credential =>
        {
            var haystack = $"{credential.Name}\n{credential.Username}\n{credential.Folder}";
            return words.All(word => haystack.Contains(word, StringComparison.CurrentCultureIgnoreCase));
        }).ToList();
    }

    /// <summary>The groups of the list, in order, each sorted by label. Empty groups are left out.</summary>
    public static IReadOnlyList<(string Title, IReadOnlyList<Credential> Items)> Groups(IReadOnlyList<Credential> credentials)
    {
        var groups = new List<(string, IReadOnlyList<Credential>)>();
        void Add(string title, Func<Credential, bool> belongs)
        {
            var items = credentials.Where(belongs).OrderBy(Label, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (items.Count > 0) groups.Add((title, items));
        }
        Add("Vault", c => c.Kind is CredentialKind.Vault);
        Add("1Password", c => c.Kind is CredentialKind.OnePassword);
        Add("Ask each time", c => c.Kind is CredentialKind.Prompt);
        return groups;
    }

    /// <summary>
    /// A button showing the chosen credential; clicking it opens the
    /// searchable list. <paramref name="changed"/> receives the new id, or
    /// null for <paramref name="noneLabel"/>.
    /// </summary>
    public static FrameworkElement Create(IReadOnlyList<Credential> credentials, string noneLabel,
                                          string? selected, Action<string?> changed, string automationId) =>
        new CredentialPickerButton(credentials, noneLabel, selected, changed, automationId);
}

/// <summary>The button behind <see cref="CredentialPicker.Create"/> and its drop-down.</summary>
internal sealed class CredentialPickerButton : Button
{
    /// <summary>Testing aid: <c>--demo-credential-search &lt;text&gt;</c> opens every picker with that search typed.</summary>
    private static readonly string? DemoSearch = App.Value("--demo-credential-search");

    private const string NoneId = "";

    private readonly IReadOnlyList<Credential> credentials;
    private readonly string noneLabel;
    private readonly Action<string?> changed;
    private readonly string automationId;
    private readonly TextBlock label = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Popup popup = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom, VerticalOffset = 2 };
    private readonly TextBox search = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center };
    private readonly ListBox list = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    private readonly TextBlock empty = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(24), Visibility = Visibility.Collapsed };
    private string? selection;

    /// <summary>Set while this code moves the highlight, which is not a choice.</summary>
    private bool highlighting;

    public CredentialPickerButton(IReadOnlyList<Credential> credentials, string noneLabel, string? selected,
                                  Action<string?> changed, string automationId)
    {
        this.credentials = credentials;
        this.noneLabel = noneLabel;
        this.changed = changed;
        this.automationId = automationId;
        selection = selected;

        HorizontalAlignment = HorizontalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        var chevron = Icons.Glyph(Icons.ChevronDown, 10);
        chevron.Margin = new Thickness(8, 1, 0, 0);
        chevron.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        var content = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(chevron, Dock.Right);
        content.Children.Add(chevron);
        content.Children.Add(label);
        Content = content;
        AutomationProperties.SetAutomationId(this, automationId);
        ShowSelection();

        BuildPopup();
        Click += (_, _) => Open(DemoSearch ?? "");
        if (DemoSearch is not null)
        {
            // Once the dialog is on screen, so the list has somewhere to appear.
            Loaded += (_, _) => Dispatcher.BeginInvoke(() => { if (IsVisible) Open(DemoSearch); }, DispatcherPriority.ApplicationIdle);
        }
        Unloaded += (_, _) => popup.IsOpen = false;
    }

    private void ShowSelection()
    {
        string text;
        if (selection is null) text = noneLabel;
        else if (credentials.FirstOrDefault(c => c.Id == selection) is { } credential) text = CredentialPicker.Label(credential);
        else text = "Missing credential";
        label.Text = text;
        // What a screen reader (and UI automation) reads as the field's value.
        AutomationProperties.SetHelpText(this, text);
    }

    // ----- The drop-down -----

    private void BuildPopup()
    {
        var glyph = Icons.Glyph(Icons.Search, 13);
        glyph.Margin = new Thickness(12, 0, 8, 0);
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        var hint = new TextBlock { Text = "Search credentials", IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");

        var clear = new Button
        {
            Content = Icons.Glyph(Icons.Close, 9),
            Width = 24,
            Height = 22,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 0, 8, 0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Focusable = false,
            ToolTip = "Clear the search",
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetAutomationId(clear, "CredentialSearchClear");
        AutomationProperties.SetName(clear, "Clear the search");
        clear.Click += (_, _) =>
        {
            search.Text = "";
            search.Focus();
        };

        AutomationProperties.SetAutomationId(search, "CredentialSearch");
        AutomationProperties.SetName(search, "Search credentials");
        search.TextChanged += (_, _) =>
        {
            hint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            clear.Visibility = search.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            Fill();
        };
        search.PreviewKeyDown += OnSearchKey;

        var field = new Grid();
        field.Children.Add(search);
        field.Children.Add(hint);

        var top = new DockPanel { Height = 38, LastChildFill = true };
        DockPanel.SetDock(glyph, Dock.Left);
        DockPanel.SetDock(clear, Dock.Right);
        top.Children.Add(glyph);
        top.Children.Add(clear);
        top.Children.Add(field);

        var rule = new Border { Height = 1 };
        rule.SetResourceReference(Border.BackgroundProperty, "ControlStrokeColorDefaultBrush");

        AutomationProperties.SetAutomationId(list, "CredentialList");
        AutomationProperties.SetName(list, "Credentials");
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        // The keyboard stays in the search field, which moves the highlight
        // itself; the rows take no focus. A click picks, and so does a
        // selection made from outside (assistive technology, UI automation).
        list.Focusable = false;
        list.PreviewMouseLeftButtonUp += OnListClick;
        list.SelectionChanged += (_, _) =>
        {
            if (!highlighting) Pick(list.SelectedItem as ListBoxItem);
        };
        empty.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        var body = new Grid();
        body.Children.Add(list);
        body.Children.Add(empty);

        var layout = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(rule, Dock.Top);
        layout.Children.Add(top);
        layout.Children.Add(rule);
        layout.Children.Add(body);

        var chrome = new Border
        {
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(1),
            // Room for the shadow, which a pop-up would otherwise clip.
            Margin = new Thickness(10, 2, 10, 14),
            Child = layout,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = 0.28, Direction = 270 },
        };
        chrome.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        chrome.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        chrome.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "TextFillColorPrimaryBrush");

        popup.Child = chrome;
        popup.PlacementTarget = this;
        popup.HorizontalOffset = -10;
        popup.Closed += (_, _) => search.Text = "";
        // Escape and Return are the pop-up's own while it is open: they must
        // not reach the dialog's Cancel and Save buttons.
        chrome.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close(refocus: true);
        };
    }

    private void Open(string query)
    {
        if (popup.Child is FrameworkElement chrome)
        {
            chrome.Width = Math.Max(ActualWidth, 420) + 20;
            chrome.Height = 360;
        }
        search.Text = query;
        Fill();
        popup.IsOpen = true;
        Dispatcher.BeginInvoke(() =>
        {
            search.Focus();
            Keyboard.Focus(search);
            search.CaretIndex = search.Text.Length;
            if (list.SelectedItem is { } highlighted) list.ScrollIntoView(highlighted);
        }, DispatcherPriority.Input);
    }

    private void Highlight(ListBoxItem? row)
    {
        highlighting = true;
        try { list.SelectedItem = row; }
        finally { highlighting = false; }
        if (row is not null && popup.IsOpen) list.ScrollIntoView(row);
    }

    private void Close(bool refocus)
    {
        popup.IsOpen = false;
        if (refocus) Focus();
    }

    /// <summary>Rebuilds the rows for what is typed and puts the highlight on something visible.</summary>
    private void Fill()
    {
        var query = search.Text.Trim();
        var groups = CredentialPicker.Groups(CredentialPicker.Matches(credentials, query));
        highlighting = true;
        try
        {
            list.Items.Clear();
            if (query.Length == 0) list.Items.Add(Row(NoneId, noneLabel, null, secondary: true));
            foreach (var (title, items) in groups)
            {
                list.Items.Add(Header(title));
                foreach (var credential in items)
                {
                    list.Items.Add(Row(credential.Id, CredentialPicker.Label(credential), credential.Folder, secondary: false));
                }
            }
        }
        finally
        {
            highlighting = false;
        }
        var nothing = groups.Count == 0 && query.Length > 0;
        empty.Text = nothing ? $"No credentials match “{query}”." : "";
        empty.Visibility = nothing ? Visibility.Visible : Visibility.Collapsed;

        // With nothing typed the highlight starts on the current choice;
        // while searching, on the best match, which is first.
        var rows = Rows().ToList();
        var wanted = query.Length == 0 ? rows.FirstOrDefault(r => (string)r.Tag == (selection ?? NoneId)) : null;
        Highlight(wanted ?? rows.FirstOrDefault());
    }

    private IEnumerable<ListBoxItem> Rows() => list.Items.OfType<ListBoxItem>().Where(item => item.Tag is string);

    private ListBoxItem Header(string title)
    {
        var text = new TextBlock { Text = title, FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 6, 0, 2) };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        // No Tag: a heading is not something to pick.
        return new ListBoxItem { Content = text, IsHitTestVisible = false, Focusable = false, IsTabStop = false, MinHeight = 0, Padding = new Thickness(8, 0, 8, 0) };
    }

    private ListBoxItem Row(string id, string text, string? detail, bool secondary)
    {
        var check = Icons.Glyph(Icons.Check, 11);
        check.Width = 18;
        check.Visibility = (selection ?? NoneId) == id ? Visibility.Visible : Visibility.Hidden;

        var name = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        if (secondary) name.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(name, 1);
        grid.Children.Add(check);
        grid.Children.Add(name);
        if (!string.IsNullOrEmpty(detail))
        {
            var where = new TextBlock
            {
                Text = detail,
                FontSize = 11,
                MaxWidth = 190,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
            };
            where.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
            Grid.SetColumn(where, 2);
            grid.Children.Add(where);
        }

        var item = new ListBoxItem
        {
            Content = grid,
            Tag = id,
            Focusable = false,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(6, 5, 10, 5),
        };
        AutomationProperties.SetAutomationId(item, "pick:" + id);
        AutomationProperties.SetName(item, text);
        if (!string.IsNullOrEmpty(detail)) AutomationProperties.SetHelpText(item, detail);
        return item;
    }

    // ----- Keys and clicks -----

    private void OnSearchKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                e.Handled = true;
                Move(1);
                break;
            case Key.Up:
                e.Handled = true;
                Move(-1);
                break;
            case Key.Enter:
                e.Handled = true;
                Pick(list.SelectedItem as ListBoxItem ?? Rows().FirstOrDefault());
                break;
        }
    }

    private void Move(int delta)
    {
        var rows = Rows().ToList();
        if (rows.Count == 0) return;
        var current = list.SelectedItem is ListBoxItem item ? rows.IndexOf(item) : -1;
        var next = current < 0 ? (delta > 0 ? 0 : rows.Count - 1) : Math.Clamp(current + delta, 0, rows.Count - 1);
        Highlight(rows[next]);
    }

    private void OnListClick(object sender, MouseButtonEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element is not null and not ListBoxItem)
        {
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        if (element is ListBoxItem { Tag: string } item)
        {
            e.Handled = true;
            Pick(item);
        }
    }

    private void Pick(ListBoxItem? item)
    {
        if (!popup.IsOpen || item?.Tag is not string id) return;
        selection = id == NoneId ? null : id;
        ShowSelection();
        Close(refocus: true);
        changed(selection);
    }
}
