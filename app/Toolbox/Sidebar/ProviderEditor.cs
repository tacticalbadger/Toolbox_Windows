using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Toolbox.Model;

namespace Toolbox.Sidebar;

/// <summary>The New/Edit Provider dialog (dynamic folders: 1Password, Active Directory).</summary>
internal sealed class ProviderEditor : EditorWindow
{
    private readonly AppModel model;
    private readonly ProviderDraft draft;
    private static readonly BooleanToVisibilityConverter BoolToVisibility = new();

    public ProviderEditor(AppModel model, ProviderDraft draft)
        : base(draft.IsNew ? "New Provider" : "Edit Provider", 600, 800)
    {
        this.model = model;
        this.draft = draft;
        DataContext = draft;
        AutomationProperties.SetAutomationId(this, "ProviderEditor");

        var form = new FormBuilder();
        form.Section("Provider");
        form.Row("Name", FormBuilder.Text(nameof(ProviderDraft.Name), hintPath: nameof(ProviderDraft.NameHint)));
        form.Row("Location", FormBuilder.Text(nameof(ProviderDraft.Parent), "Top level; use / for subfolders"));
        form.Note("Appears as a folder named after the provider; what it generates lands inside.");
        var source = new ComboBox { ItemsSource = ProviderKindChoices.All, HorizontalAlignment = HorizontalAlignment.Stretch };
        source.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedItemProperty,
                          new Binding(nameof(ProviderDraft.SelectedKind)) { Mode = BindingMode.TwoWay });
        AutomationProperties.SetAutomationId(source, "Source");
        form.Row("Source", source);
        form.Row("Refresh", RefreshStepper());
        form.Note("Automatic refresh also runs when Toolbox starts.");

        form.Section("1Password", nameof(ProviderDraft.IsOnePassword));
        form.Row("op path", FormBuilder.Text(nameof(ProviderDraft.OpPath), CoreApi.DetectOpPath() ?? @"C:\Program Files\1Password CLI\op.exe"));
        form.Row("Account", FormBuilder.Text(nameof(ProviderDraft.Account), "All accounts"));
        form.Row("Vaults", FormBuilder.Text(nameof(ProviderDraft.Vaults), "All vaults; separate names with commas"));
        form.Note("Items become credentials, grouped by vault. Uses the desktop app integration: turn on " +
                  "“Integrate with 1Password CLI” under 1Password > Settings > Developer, then the first sync " +
                  "asks 1Password to confirm it's you.");

        form.Section("Active Directory", nameof(ProviderDraft.IsActiveDirectory));
        form.Row("Domain controller", FormBuilder.Text(nameof(ProviderDraft.Server), "dc01.corp.example"));
        form.Row("Search base", FormBuilder.Text(nameof(ProviderDraft.SearchBase), "DC=corp,DC=example"));

        foreach (var role in draft.Roles) RoleSection(form, role);

        form.Section("Connection security", nameof(ProviderDraft.IsActiveDirectory));
        form.Wide(FormBuilder.Toggle("Fall back to unencrypted LDAP if LDAPS fails", nameof(ProviderDraft.AllowPlainLdap)));
        form.Wide(FormBuilder.Toggle("Accept untrusted certificates", nameof(ProviderDraft.AcceptInvalidCerts)));
        form.Note("Computers become connections: Windows → RDP, macOS → VNC, Linux → SSH. OUs become folders.");
        SetForm(form);

        AddButton("Save and Sync", () => Save(syncNow: true), isDefault: true, automationId: "EditorSaveAndSync");
        AddButton("Save", () => Save(syncNow: false), automationId: "EditorSave");
        AddButton("Cancel", Close, isCancel: true, automationId: "EditorCancel");
    }

    public Provider? Saved { get; private set; }

    /// <summary>Whether the user asked for a sync right after saving.</summary>
    public bool SyncRequested { get; private set; }

    private FrameworkElement RefreshStepper()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var less = new Button { Content = "−", Width = 34, Padding = new Thickness(0, 3, 0, 3), ToolTip = "15 minutes less" };
        var more = new Button { Content = "+", Width = 34, Padding = new Thickness(0, 3, 0, 3), ToolTip = "15 minutes more" };
        less.Click += (_, _) => draft.AutoRefreshMinutes -= 15;
        more.Click += (_, _) => draft.AutoRefreshMinutes += 15;
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0), MinWidth = 150 };
        label.SetBinding(TextBlock.TextProperty, new Binding(nameof(ProviderDraft.RefreshLabel)) { Mode = BindingMode.OneWay });
        AutomationProperties.SetAutomationId(label, "RefreshLabel");
        AutomationProperties.SetAutomationId(less, "RefreshLess");
        AutomationProperties.SetAutomationId(more, "RefreshMore");
        panel.Children.Add(less);
        panel.Children.Add(label);
        panel.Children.Add(more);
        return panel;
    }

    /// <summary>One account of the Active Directory provider: none, an existing credential, or typed in here.</summary>
    private void RoleSection(FormBuilder form, RoleCredentialDraft role)
    {
        var section = form.Section(role.Title, dataContext: role);
        // The section's own data is the role; whether it shows depends on the provider's source.
        section.SetBinding(VisibilityProperty, new Binding(nameof(ProviderDraft.IsActiveDirectory))
        {
            Source = draft,
            Mode = BindingMode.OneWay,
            Converter = BoolToVisibility,
        });
        var prefix = role.Role.ToString();
        form.Row("Credential", FormBuilder.Choice(nameof(RoleCredentialDraft.ModeChoices), nameof(RoleCredentialDraft.SelectedMode), prefix + "Mode"));
        var picker = CredentialPicker.Create(model.Credentials, "Choose…", role.CredentialId,
                                             id => role.CredentialId = id, prefix + "Credential");
        form.Row("Use", picker, nameof(RoleCredentialDraft.IsExisting));
        form.Row("Username", FormBuilder.Text(nameof(RoleCredentialDraft.Username), @"DOMAIN\user or user@domain", automationId: prefix + "Username"),
                 nameof(RoleCredentialDraft.IsSpecify));
        form.Row("Password", FormBuilder.Secret(value => role.Password = value, prefix + "Password"),
                 nameof(RoleCredentialDraft.IsSpecify));
        form.Note(null, nameof(RoleCredentialDraft.IsSpecify), textPath: nameof(RoleCredentialDraft.PasswordNote));
    }

    private async void Save(bool syncNow)
    {
        if (draft.ValidationError is { } problem)
        {
            ShowValidation(problem);
            return;
        }
        ShowValidation(null);
        if (draft.IsNew) draft.Id = CoreApi.NewId();
        SetBusy(true);
        try
        {
            Saved = await model.SaveProviderAsync(draft);
            SyncRequested = syncNow;
            Accept();
        }
        catch (VaultException e) when (e.IsCancellation)
        {
            // Backed out of unlocking the vault: stay in the form.
        }
        catch (Exception e)
        {
            Prompts.Error("Couldn't save the provider", Errors.Describe(e));
        }
        finally
        {
            SetBusy(false);
        }
    }
}
