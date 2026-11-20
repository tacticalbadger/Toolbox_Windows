using System.Windows;
using Toolbox.Files;
using Toolbox.Model;
using Toolbox.Remote;
using Toolbox.Sidebar;
using Toolbox.Terminal;
using Toolbox.Workspaces;

namespace Toolbox;

/// <summary>Opening sessions, and everything the sidebar asks the window to do.</summary>
internal partial class MainWindow
{
    // ----- Connections -----

    public void OpenConnection(Connection connection) => Open(connection);

    public void BrowseFiles(Connection connection) => OpenFiles(connection);

    /// <summary>Opens a connection in <paramref name="target"/>, or in the pane a new session should use.</summary>
    internal async void Open(Connection connection, PaneView? target = null)
    {
        if (connection.Kind.IsFileTransfer())
        {
            OpenFiles(connection, target);
            return;
        }
        if (connection.Kind == ConnectionKind.Vnc)
        {
            Prompts.Info("VNC sessions aren't in this build yet",
                         "SSH, RDP and file transfer work today. The VNC pane is still being ported from the Mac app.");
            return;
        }

        var panes = Panes;
        var pane = target ?? panes.PaneForNewSession();
        if (connection.Kind == ConnectionKind.Rdp)
        {
            var desktop = new RdpPane(connection);
            pane.SetContent(desktop);
            panes.SetFocus(pane.Id, activate: true);
            try
            {
                var resolved = await CredentialResolver.PasswordAuthAsync(connection);
                if (resolved is null) desktop.Cancelled();
                else desktop.Start(resolved.FullUsername, resolved.Password);
            }
            catch (VaultException e) when (e.IsCancellation)
            {
                desktop.Cancelled();
            }
            catch (Exception e)
            {
                desktop.Failed(Errors.Describe(e));
            }
            return;
        }

        var session = new TerminalPane(connection);
        pane.SetContent(session);
        panes.SetFocus(pane.Id, activate: true);
        try
        {
            var resolved = await CredentialResolver.SshAuthAsync(connection);
            if (resolved is null)
            {
                session.Cancelled();
                return;
            }
            session.Start(resolved.Username, resolved.Auth);
        }
        catch (VaultException e) when (e.IsCancellation)
        {
            session.Cancelled();
        }
        catch (Exception e)
        {
            session.Failed(Errors.Describe(e));
        }
    }

    /// <summary>
    /// Opens a file browser: SFTP for SSH and SFTP connections, FTP or FTPS
    /// for those kinds.
    /// </summary>
    internal async void OpenFiles(Connection connection, PaneView? target = null)
    {
        if (!connection.Kind.UsesSsh() && !connection.Kind.IsFileTransfer())
        {
            Prompts.Info("File browsing needs SSH or FTP",
                         $"This connection is {connection.Kind.Label()}. Create a File Transfer connection for the host instead.");
            return;
        }
        var panes = Panes;
        var pane = target ?? panes.PaneForNewSession();
        var files = new FileBrowserPane(connection);
        pane.SetContent(files);
        panes.SetFocus(pane.Id, activate: true);
        try
        {
            if (connection.Kind.UsesSsh())
            {
                var resolved = await CredentialResolver.SshAuthAsync(connection);
                if (resolved is null) files.Cancelled();
                else files.Start(resolved.Username, resolved.Auth);
            }
            else
            {
                var resolved = await CredentialResolver.FtpAuthAsync(connection);
                if (resolved is null) files.Cancelled();
                else files.StartFtp(resolved.Value.Username, resolved.Value.Password, secure: connection.Kind == ConnectionKind.Ftps);
            }
        }
        catch (VaultException e) when (e.IsCancellation)
        {
            files.Cancelled();
        }
        catch (Exception e)
        {
            files.Failed(Errors.Describe(e));
        }
    }

    public void EditConnection(Connection? connection, string? folder)
    {
        var draft = connection is null ? new ConnectionDraft() : new ConnectionDraft(connection);
        if (connection is null && folder is not null)
        {
            draft.Folder = folder;
            // A folder that hands down a credential is the natural default.
            if (model.InheritedCredential(folder) is not null) draft.AuthMethod = AuthMethod.Folder;
        }
        new ConnectionEditor(model, draft) { Owner = this }.ShowDialog();
    }

    public void DeleteConnection(Connection connection)
    {
        if (!Prompts.Confirm($"Delete “{connection.Name}”?",
                             "This removes the connection from the sidebar. Open sessions are not affected.", "Delete"))
        {
            return;
        }
        try { model.Delete(connection); }
        catch (Exception e) { Prompts.Error("Couldn't delete the connection", Errors.Describe(e)); }
    }

    // ----- Credentials -----

    public void EditCredential(Credential? credential, string? folder)
    {
        var draft = credential is null ? new CredentialDraft() : new CredentialDraft(credential);
        if (credential is null && folder is not null) draft.Folder = folder;
        new CredentialEditor(model, draft) { Owner = this }.ShowDialog();
    }

    public void DeleteCredential(Credential credential)
    {
        var users = model.Store.ConnectionsUsingCredential(credential.Id);
        var folders = model.Store.FoldersUsingCredential(credential.Id);
        var message = credential.Kind is CredentialKind.Vault
            ? "The stored secret is deleted from the vault."
            : "The credential is removed from Toolbox.";
        if (users.Length > 0)
        {
            message += $" {users.Length} connection{(users.Length == 1 ? "" : "s")} still reference it and will ask for a password instead.";
        }
        if (folders.Length > 0)
        {
            message += $" {folders.Length} folder{(folders.Length == 1 ? " passes" : "s pass")} it down to connections; they lose it.";
        }
        if (!Prompts.Confirm($"Delete “{credential.Name}”?", message, "Delete")) return;
        try { model.DeleteCredential(credential); }
        catch (Exception e) { Prompts.Error("Couldn't delete the credential", Errors.Describe(e)); }
    }

    // ----- Providers -----

    public void EditProvider(Provider? provider, string? folder)
    {
        var draft = provider is null ? new ProviderDraft() : new ProviderDraft(provider, model);
        if (provider is null && folder is not null) draft.Parent = folder;
        var editor = new ProviderEditor(model, draft) { Owner = this };
        if (editor.ShowDialog() == true && editor.SyncRequested && editor.Saved is { } saved)
        {
            SyncProvider(saved);
        }
    }

    public void DeleteProvider(Provider provider)
    {
        if (!Prompts.Confirm($"Delete “{provider.Name}”?",
                             "Everything it generated is removed from the sidebar too.", "Delete"))
        {
            return;
        }
        try { model.DeleteProvider(provider); }
        catch (Exception e) { Prompts.Error("Couldn't delete the provider", Errors.Describe(e)); }
    }

    public async void SyncProvider(Provider provider) =>
        await ProviderSync.Shared.SyncAsync(provider, interactive: true);

    // ----- Folders -----

    public void NewFolder(string? parent)
    {
        var location = parent is null ? "At the top level" : $"Inside {FolderPath.Leaf(parent)}";
        var name = Prompts.AskText("New Folder", location, "untitled folder", "Create");
        if (string.IsNullOrWhiteSpace(name)) return;
        try { model.AddFolder(FolderPath.Join(parent, name)); }
        catch (Exception e) { Prompts.Error("Couldn't create the folder", Errors.Describe(e)); }
    }

    public void RenameFolder(string path)
    {
        var leaf = FolderPath.Leaf(path);
        var name = Prompts.AskText("Rename Folder", path, leaf, "Rename");
        if (string.IsNullOrWhiteSpace(name) || name == leaf) return;
        try { model.RenameFolder(path, FolderPath.Join(FolderPath.Parent(path), name)); }
        catch (Exception e) { Prompts.Error("Couldn't rename the folder", Errors.Describe(e)); }
    }

    public void DeleteFolder(string path)
    {
        if (!Prompts.Confirm($"Delete folder “{FolderPath.Leaf(path)}”?", "Only empty folders can be deleted.", "Delete"))
        {
            return;
        }
        try { model.DeleteFolder(path); }
        catch (Exception e) { Prompts.Error("Couldn't delete the folder", Errors.Describe(e)); }
    }

    public void SetFolderCredential(string path) =>
        new FolderCredentialDialog(model, path) { Owner = this }.ShowDialog();

    // ----- Launch arguments -----

    /// <summary>
    /// <c>--connect &lt;name or id&gt;</c> opens that connection on launch and
    /// <c>--files &lt;name or id&gt;</c> its file browser (both may repeat). The
    /// rest are testing aids for the end-to-end harness: each drives a path a
    /// person would reach with the mouse.
    /// </summary>
    internal void RunLaunchArguments()
    {
        var args = App.Arguments;
        for (var i = 0; i + 1 < args.Count; i++)
        {
            if (args[i] is not ("--connect" or "--files")) continue;
            var wanted = args[i + 1];
            var connection = model.FindConnection(wanted);
            if (connection is null)
            {
                Log.Warn($"{args[i]}: no connection named {wanted}");
                continue;
            }
            if (args[i] == "--connect") Open(connection);
            else OpenFiles(connection);
        }

        // Move the focused pane into a fresh workspace tab.
        if (App.HasFlag("--demo-move")) After(1.5, MovePaneToNewWorkspace);

        // Close the focused pane shortly after launch, the way its close button would.
        if (App.HasFlag("--demo-close")) After(3, () => Panes.ClosePane(Panes.FocusedPane.Id));

        // Open an editor straight away.
        if (App.Value("--edit-connection") is { } editConnection)
        {
            After(0.3, () => EditConnection(editConnection == "new" ? null : model.FindConnection(editConnection), null));
        }
        if (App.Value("--edit-credential") is { } editCredential)
        {
            After(0.3, () => EditCredential(
                editCredential == "new" ? null : model.Credentials.FirstOrDefault(c => c.Id == editCredential || c.Name == editCredential), null));
        }
        if (App.Value("--edit-provider") is { } editProvider)
        {
            After(0.3, () => EditProvider(
                editProvider == "new" ? null : model.Providers.FirstOrDefault(p => p.Id == editProvider || p.Name == editProvider), null));
        }
        if (App.Value("--set-folder-credential") is { } folder) After(0.3, () => SetFolderCredential(folder));

        // Exercise a provider-owned account's save and removal.
        if (App.Value("--demo-provider-account") is { } providerId && model.Provider(providerId) is { } provider)
        {
            _ = DemoProviderAccountAsync(provider);
        }

        // Unlock the vault right away and log how it went.
        if (App.HasFlag("--unlock-vault")) _ = DemoUnlockAsync();
    }

    private void After(double seconds, Action action)
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
    }

    private static async Task DemoUnlockAsync()
    {
        try
        {
            await VaultManager.Shared.UnlockedVaultAsync();
            Log.Info("vault unlocked");
        }
        catch (Exception e)
        {
            Log.Warn($"vault unlock failed: {e.Message}");
        }
    }

    private async Task DemoProviderAccountAsync(Provider provider)
    {
        try
        {
            var draft = new ProviderDraft(provider, model);
            draft.Bind.Mode = RoleMode.Specify;
            draft.Bind.Username = @"CORP\svc-toolbox";
            draft.Bind.Password = "fixture-password";
            await model.SaveProviderAsync(draft);
            var managed = model.ManagedCredentials(provider.Id);
            var managedId = ProviderRole.Bind.ManagedId(provider.Id);
            var stored = VaultManager.Shared.Vault.GetSecret(managedId);
            Log.Info($"provider-account: saved; managed=[{string.Join(",", managed.Select(c => c.Id))}] " +
                     $"folder={managed.FirstOrDefault()?.Folder ?? "-"} vaultPasswordStored={stored.Password == "fixture-password"}");

            draft = new ProviderDraft(model.Provider(provider.Id)!, model);
            Log.Info($"provider-account: reloaded draft mode={draft.Bind.Mode} hasStored={draft.Bind.HasStored}");
            draft.Bind.Mode = RoleMode.Existing;
            draft.Bind.CredentialId = "op-fake:i1";
            await model.SaveProviderAsync(draft);
            var after = model.ManagedCredentials(provider.Id);
            var bindNow = (model.Provider(provider.Id)?.Kind as ProviderKind.ActiveDirectory)?.BindCredentialId;
            var gone = !VaultManager.Shared.Vault.HasSecret(managedId);
            Log.Info($"provider-account: switched; managed=[{string.Join(",", after.Select(c => c.Id))}] " +
                     $"bind={bindNow ?? "-"} vaultSecretRemoved={gone}");
        }
        catch (Exception e)
        {
            Log.Warn($"provider-account: failed: {Errors.Describe(e)}");
        }
    }
}
