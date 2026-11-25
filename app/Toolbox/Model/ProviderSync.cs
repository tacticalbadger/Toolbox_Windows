using System.Windows.Threading;

namespace Toolbox.Model;

/// <summary>Runs providers (dynamic folders) and keeps them fresh.</summary>
internal sealed class ProviderSync
{
    public static ProviderSync Shared { get; } = new();

    private readonly Dictionary<string, DispatcherTimer> timers = new();
    private readonly HashSet<string> inProgress = new();

    /// <summary>Raised when a provider starts or finishes syncing.</summary>
    public event Action? Changed;

    public bool IsSyncing(string providerId) => inProgress.Contains(providerId);

    /// <summary>Syncs every provider; with <paramref name="automatic"/> only those with auto-refresh on.</summary>
    public async Task SyncAllAsync(bool automatic)
    {
        foreach (var provider in AppModel.Shared.Providers.ToList())
        {
            if (automatic && provider.AutoRefreshMinutes == 0) continue;
            await SyncAsync(provider, interactive: !automatic);
        }
    }

    /// <summary>
    /// Runs one provider. Interactive syncs report failures in a dialog;
    /// automatic ones just record the status on the provider.
    /// </summary>
    public async Task<SyncResult?> SyncAsync(Provider provider, bool interactive)
    {
        if (!inProgress.Add(provider.Id)) return null;
        Changed?.Invoke();
        try
        {
            BindCredentials? bind = null;
            if (provider.Kind is ProviderKind.ActiveDirectory directory)
            {
                var credential = directory.BindCredentialId is { } id ? AppModel.Shared.Credential(id) : null;
                if (credential is null)
                {
                    throw new InvalidOperationException("Choose the credential to bind with on the provider first.");
                }
                bind = await CredentialResolver.BindAsync(credential);
                if (bind is null) return null;
            }
            var result = await AppModel.Shared.Store.SyncProvider(provider.Id, bind);
            AppModel.Shared.Reload();
            Log.Info($"provider {CurrentName(provider)}: synced, {result.Connections} connections, {result.Credentials} credentials, {result.Warnings.Length} warnings");
            if (interactive && result.Warnings.Length > 0)
            {
                Prompts.Info($"{CurrentName(provider)} synced with warnings", string.Join("\n\n", result.Warnings));
            }
            return result;
        }
        catch (Exception error)
        {
            AppModel.Shared.Reload();
            if (error is VaultException { IsCancellation: true })
            {
                return null;
            }
            // Deleted while it ran: there is nothing left to report on.
            if (AppModel.Shared.Provider(provider.Id) is null)
            {
                Log.Info($"provider {provider.Name}: deleted while it synced; the results were dropped");
                return null;
            }
            if (interactive)
            {
                Prompts.Error($"Couldn't sync {CurrentName(provider)}", Errors.Describe(error));
            }
            else
            {
                Log.Warn($"provider {CurrentName(provider)} auto-sync failed: {Errors.Describe(error)}");
            }
            return null;
        }
        finally
        {
            inProgress.Remove(provider.Id);
            Changed?.Invoke();
        }
    }

    /// <summary>The provider's name now; it can be renamed while a sync runs.</summary>
    private static string CurrentName(Provider provider) =>
        AppModel.Shared.Provider(provider.Id)?.Name ?? provider.Name;

    /// <summary>(Re)arms periodic refresh for providers that want it.</summary>
    public void ScheduleAutoRefresh()
    {
        foreach (var timer in timers.Values) timer.Stop();
        timers.Clear();
        foreach (var provider in AppModel.Shared.Providers)
        {
            if (provider.AutoRefreshMinutes == 0) continue;
            var id = provider.Id;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(provider.AutoRefreshMinutes) };
            timer.Tick += async (_, _) =>
            {
                var current = AppModel.Shared.Provider(id);
                if (current is not null) await SyncAsync(current, interactive: false);
            };
            timer.Start();
            timers[id] = timer;
        }
    }
}
