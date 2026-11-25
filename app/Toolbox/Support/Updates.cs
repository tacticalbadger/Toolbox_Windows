using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace Toolbox.Support;

/// <summary>A reason an update did not happen, in words for the user.</summary>
internal sealed class UpdateException(string message) : Exception(message);

/// <summary>Where a feed and its packages are read from.</summary>
internal interface IUpdateSource
{
    string Description { get; }

    /// <summary>Reads <c>update.json</c> or <c>update.json.sig</c>; more than <paramref name="limit"/> bytes is an error.</summary>
    Task<byte[]> ReadAsync(string name, int limit, CancellationToken cancel);

    /// <summary>Fetches a version's package into <paramref name="to"/>, giving up once it is longer than it should be.</summary>
    Task DownloadAsync(string version, string file, string to, long size, IProgress<double> progress, CancellationToken cancel);
}

/// <summary>
/// Updates over the air.
///
/// Once a day, and when asked from Help > Check for Updates, the app reads a
/// small file attached to the newest GitHub Release (update.json). What it
/// finds there is offered with its release notes and installed when the user
/// agrees; installing restarts the app, so it asks first when sessions are
/// open.
///
/// Nothing is taken on the server's word. The file has to carry a signature
/// made with the update key whose public half is below, it names the
/// package's length and SHA-256, and the download has to match both before
/// it is unpacked. So whoever controls the place the files are served from
/// can withhold an update but not supply one. The builds are not
/// code-signed: this key is all that stands between a download and its
/// installation, and there is no second way in (see docs/SECURITY-REVIEW.md,
/// the 0.3.0 addendum).
///
/// A check sends a request for that file to github.com, with the app's
/// version, build number and processor type in the User-Agent, and nothing
/// about connections or credentials.
/// </summary>
internal sealed class Updates
{
    public static Updates Shared { get; } = new();

    /// <summary>
    /// The public half of the update key (ECDSA P-256; X and Y in Base64).
    /// The private half is in the Windows key store of the account releases
    /// are made from (tools\update-key.ps1). Changing this cuts every
    /// installed copy off from updates.
    /// </summary>
    internal const string PublicKey = "BVp5rdfaL6V6qrkCTUienO3s15qjXeW/FHcNjMbOezilAzAl+wiNZX8e2Upah3+i2oyj8uwiGMOkyP2TEfhTPA==";

    /// <summary>
    /// The releases this app is updated from. The address is built into
    /// every copy: the repository cannot be renamed or moved without a
    /// release that knows the new address first.
    /// </summary>
    private const string Repository = "https://github.com/tacticalbadger/Toolbox_Windows";

    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(24);

    private MainWindow? window;
    private IUpdateSource? source;
    private string publicKey = PublicKey;
    private int currentBuild = AppInfo.Build;
    private bool installsWithoutAsking;
    private bool checking;
    private DispatcherTimer? timer;

    /// <summary>"win-arm64" or "win-x64": the kind of package this copy is.</summary>
    public static string Architecture { get; } =
        RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "win-arm64" : "win-x64";

    /// <summary>Whether this run looks for updates at all.</summary>
    public bool IsActive => source is not null;

    /// <summary>The daily look. On unless the user switches it off.</summary>
    public bool ChecksAutomatically
    {
        get => AppSettings.Shared.CheckForUpdates;
        set
        {
            AppSettings.Shared.CheckForUpdates = value;
            AppSettings.Shared.SaveSoon();
            Log.Info($"updates: automatic checks {(value ? "on" : "off")}");
        }
    }

    /// <summary>Decides whether this run looks for updates, and starts the daily look if it does.</summary>
    public void Start(MainWindow owner)
    {
        window = owner;
        if (!AppPaths.UsesDefaultStore)
        {
            // A run against a store named in the environment is a test run.
            // It must not find the real release and offer to replace a test
            // copy with it; the update rehearsal names a feed of its own
            // instead, with its own key. The everyday app takes neither
            // from the command line.
            if (App.Value("--updater-feed") is { } feed)
            {
                // A folder, or "github" for the real releases (to see a
                // test copy take a published version).
                source = feed == "github" ? new WebSource(Repository) : new FolderSource(feed);
                publicKey = App.Value("--updater-key") ?? PublicKey;
                if (int.TryParse(App.Value("--updater-pretend-build"), out var pretended)) currentBuild = pretended;
                installsWithoutAsking = App.HasFlag("--demo-update-install");
            }
        }
        else if (AppInfo.IsPackaged && AppInfo.Build > 0)
        {
            // A build made for handing out. One made for trying things
            // (tools\build.ps1 app) is not replaced by a release.
            source = new WebSource(Repository);
        }
        CleanUpLater();
        if (!IsActive)
        {
            Log.Info("updates: this run does not look for updates");
            return;
        }

        timer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        timer.Tick += (_, _) => CheckIfDue();
        timer.Start();
        // Not in the first moments of a launch.
        var soon = new DispatcherTimer { Interval = TimeSpan.FromSeconds(App.HasFlag("--demo-update") ? 2 : 8) };
        soon.Tick += (_, _) =>
        {
            soon.Stop();
            if (App.HasFlag("--demo-update") && !AppPaths.UsesDefaultStore) _ = CheckAsync(quiet: true);
            else CheckIfDue();
        };
        soon.Start();
    }

    private void CheckIfDue()
    {
        if (!ChecksAutomatically) return;
        var last = AppSettings.Shared.LastUpdateCheck;
        if (last is null || DateTimeOffset.UtcNow - last.Value >= CheckEvery || last.Value > DateTimeOffset.UtcNow)
        {
            _ = CheckAsync(quiet: true);
        }
    }

    /// <summary>Help > Check for Updates: looks now and says what it found.</summary>
    public Task CheckNowAsync() => CheckAsync(quiet: false);

    /// <summary>
    /// Looks for an update. A quiet look (the daily one) shows something
    /// only when there is an update to offer; failures go to the log.
    /// </summary>
    private async Task CheckAsync(bool quiet)
    {
        if (checking || source is null || window is null) return;
        checking = true;
        try
        {
            Log.Info($"updates: looking for an update ({source.Description})");
            var data = await source.ReadAsync("update.json", UpdateFeed.MaxFeedBytes, CancellationToken.None);
            var signature = Encoding.ASCII.GetString(await source.ReadAsync("update.json.sig", UpdateFeed.MaxSignatureBytes, CancellationToken.None));
            if (!UpdateFeed.IsSigned(data, signature, publicKey))
            {
                throw new UpdateException("The update information is not signed with Toolbox's update key, so it was not used.");
            }
            var feed = UpdateFeed.Parse(data);
            AppSettings.Shared.LastUpdateCheck = DateTimeOffset.UtcNow;
            AppSettings.Shared.SaveSoon();

            if (!feed.IsNewerThan(currentBuild))
            {
                Log.Info($"updates: nothing newer in the feed (it has {feed.Version}, build {feed.Build}; this is build {currentBuild})");
                if (!quiet) Prompts.Info("Toolbox is up to date", $"{AppInfo.Display} is the newest version.");
                return;
            }
            Log.Info($"updates: found {feed.Version} (build {feed.Build})");
            if (!feed.Packages.TryGetValue(Architecture, out var package))
            {
                Log.Warn($"updates: {feed.Version} has no package for {Architecture}");
                if (!quiet) Prompts.Info("No update for this PC yet", $"Toolbox {feed.Version} is out, but not for this kind of PC ({Architecture}).");
                return;
            }
            if (installsWithoutAsking || UpdateWindow.Offer(feed))
            {
                await InstallAsync(feed, package);
            }
            else
            {
                Log.Info($"updates: {feed.Version} was put off");
            }
        }
        catch (Exception e) when (e is UpdateException or HttpRequestException or IOException or FormatException
                                      or TaskCanceledException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Warn($"updates: stopped: {e.Message}");
            if (!quiet) Prompts.Error("Couldn't check for updates", e is UpdateException or FormatException ? e.Message : Errors.Describe(e));
        }
        finally
        {
            checking = false;
        }
    }

    private async Task InstallAsync(UpdateFeed feed, UpdatePackage package)
    {
        if (window is null || source is null) return;
        var installed = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        if (!UpdateInstaller.CanWrite(installed))
        {
            Log.Warn($"updates: no write access to {installed}; {feed.Version} was not installed");
            Prompts.Info("Toolbox can't update itself here",
                         $"Toolbox is in a folder it may not change:\n{installed}\n\nMove it to a folder of your own (for example " +
                         "%LOCALAPPDATA%\\Programs), or download the new version from the Releases page and replace this folder.");
            return;
        }
        // Installing restarts the app: asked before anything is downloaded.
        if (!installsWithoutAsking && !window.ConfirmRestartForUpdate()) return;

        var folder = Path.Combine(AppPaths.UpdatesDirectory, $"{feed.Version}-{feed.Build}");
        Directory.CreateDirectory(folder);
        var zip = Path.Combine(folder, package.File);
        using var cancel = new CancellationTokenSource();
        var progress = installsWithoutAsking ? null : UpdateWindow.ShowProgress(feed, cancel);
        string app;
        try
        {
            await source.DownloadAsync(feed.Version, package.File, zip, package.Size,
                                       new Progress<double>(done => progress?.Report(done)), cancel.Token);
            Log.Info($"updates: downloaded {feed.Version}");
            if (!await Task.Run(() => UpdateInstaller.Matches(zip, package)))
            {
                File.Delete(zip);
                throw new UpdateException("The downloaded update is not the file the release described, so it was not installed.");
            }
            app = await Task.Run(() => UpdateInstaller.Unpack(zip, Path.Combine(folder, "app")));
        }
        catch (OperationCanceledException)
        {
            Log.Info($"updates: the download of {feed.Version} was cancelled");
            return;
        }
        finally
        {
            progress?.Close();
        }

        Log.Info($"updates: {feed.Version} is verified and will be installed now");
        var start = new ProcessStartInfo(Path.Combine(app, UpdateInstaller.Executable)) { UseShellExecute = false, WorkingDirectory = app };
        start.ArgumentList.Add("--apply-update");
        start.ArgumentList.Add(installed);
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        Process.Start(start)?.Dispose();
        window.CloseForUpdate();
    }

    /// <summary>
    /// Removes what earlier updates left in the updates folder, a while
    /// after starting (the copy that installed this version may still be
    /// closing). Not from a copy that is itself running from there.
    /// </summary>
    private static void CleanUpLater()
    {
        var folder = AppPaths.UpdatesDirectory;
        if (AppContext.BaseDirectory.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(20));
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Next start tries again.
            }
        });
    }

    // ----- Sources -----

    /// <summary>GitHub Releases: the feed from the newest release, a package from its version's release.</summary>
    private sealed class WebSource(string repository) : IUpdateSource
    {
        private static readonly HttpClient Client = CreateClient();

        public string Description => "GitHub Releases";

        private static HttpClient CreateClient()
        {
            // Redirects are followed (GitHub serves release files from another host), but never to plain HTTP.
            var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 })
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"Toolbox/{AppInfo.Version} (build {AppInfo.Build}; {Architecture})");
            return client;
        }

        public async Task<byte[]> ReadAsync(string name, int limit, CancellationToken cancel)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var response = await Client.GetAsync($"{repository}/releases/latest/download/{name}",
                                                       HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            return await ReadLimitedAsync(stream, limit, timeout.Token);
        }

        public async Task DownloadAsync(string version, string file, string to, long size, IProgress<double> progress, CancellationToken cancel)
        {
            using var response = await Client.GetAsync($"{repository}/releases/download/v{version}/{file}",
                                                       HttpCompletionOption.ResponseHeadersRead, cancel);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancel);
            await CopyLimitedAsync(stream, to, size, progress, cancel);
        }
    }

    /// <summary>A folder holding update.json, its signature and the packages: the update rehearsal's feed.</summary>
    private sealed class FolderSource(string folder) : IUpdateSource
    {
        public string Description => "a folder, for testing";

        public async Task<byte[]> ReadAsync(string name, int limit, CancellationToken cancel)
        {
            await using var stream = File.OpenRead(Path.Combine(folder, name));
            return await ReadLimitedAsync(stream, limit, cancel);
        }

        public async Task DownloadAsync(string version, string file, string to, long size, IProgress<double> progress, CancellationToken cancel)
        {
            await using var stream = File.OpenRead(Path.Combine(folder, file));
            await CopyLimitedAsync(stream, to, size, progress, cancel);
        }
    }

    private static async Task<byte[]> ReadLimitedAsync(Stream stream, int limit, CancellationToken cancel)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancel)) > 0)
        {
            if (buffer.Length + read > limit) throw new UpdateException("The update information is larger than it can be, so it was not used.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static async Task CopyLimitedAsync(Stream from, string to, long size, IProgress<double> progress, CancellationToken cancel)
    {
        await using var file = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None);
        var chunk = new byte[128 * 1024];
        long total = 0;
        int read;
        while ((read = await from.ReadAsync(chunk, cancel)) > 0)
        {
            total += read;
            if (total > size) throw new UpdateException("The downloaded update is larger than the release said, so it was not installed.");
            await file.WriteAsync(chunk.AsMemory(0, read), cancel);
            progress.Report((double)total / size);
        }
    }
}
