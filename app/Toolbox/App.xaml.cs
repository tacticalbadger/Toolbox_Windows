using System.Windows;
using System.Windows.Threading;
using Toolbox.Model;

namespace Toolbox;

public partial class App : Application
{
    /// <summary>The command line without the program name.</summary>
    internal static IReadOnlyList<string> Arguments { get; private set; } = [];

    internal static bool HasFlag(string flag) => Arguments.Contains(flag);

    /// <summary>The value following <paramref name="flag"/>, if it is there.</summary>
    internal static string? Value(string flag)
    {
        for (var i = 0; i + 1 < Arguments.Count; i++)
        {
            if (Arguments[i] == flag) return Arguments[i + 1];
        }
        return null;
    }

    /// <summary>The <paramref name="count"/> values following <paramref name="flag"/>, if they are all there.</summary>
    internal static string[]? Values(string flag, int count)
    {
        for (var i = 0; i + count < Arguments.Count; i++)
        {
            if (Arguments[i] == flag) return Arguments.Skip(i + 1).Take(count).ToArray();
        }
        return null;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Arguments = e.Args;
        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("unhandled exception (fatal): " + args.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("unobserved task exception", args.Exception);
            args.SetObserved();
        };

        // A freshly unpacked copy started by the running app to install
        // itself (see UpdateInstaller): it swaps the files, starts the app
        // from its usual folder, and is done. It opens no store and no window.
        if (e.Args is ["--apply-update", var installed, var oldProcess] && int.TryParse(oldProcess, out var processId))
        {
            Log.Info($"Toolbox {AppInfo.Display} started to install itself into {installed}");
            installing = true;
            var problem = UpdateInstaller.Apply(installed, processId);
            if (problem is not null) Prompts.Error("The update was not installed", problem);
            Shutdown();
            return;
        }

        Log.Info($"Toolbox {AppInfo.Display} starting (core {CoreApi.CoreVersion()}, {Environment.OSVersion.VersionString}, " +
                 $"{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})");
        Log.Info($"store: {AppPaths.StorePath}");

        // A copy the installer put here says which version it is now (it may have updated itself since).
        InstalledCopy.RecordVersion();

        // Shells of local terminals that an earlier run left without a window when it was killed.
        Terminal.TerminalLedger.EndOrphans();

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        // Providers with auto-refresh run at launch, then on their timers.
        await ProviderSync.Shared.SyncAllAsync(automatic: true);
        ProviderSync.Shared.ScheduleAutoRefresh();
        window.RunLaunchArguments();
        Updates.Shared.Start(window);
    }

    /// <summary>This process only installs an update: it has no settings to save.</summary>
    private bool installing;

    protected override void OnExit(ExitEventArgs e)
    {
        if (installing)
        {
            base.OnExit(e);
            return;
        }
        AppSettings.Shared.Save();
        Log.Info("Toolbox exiting");
        base.OnExit(e);
    }

    /// <summary>
    /// A failure in one handler should not close every session the user has
    /// open: log it, say what happened, and keep running.
    /// </summary>
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("unhandled exception on the UI thread: " + e.Exception);
        e.Handled = true;
        try
        {
            Prompts.Error("Something went wrong", Errors.Describe(e.Exception));
        }
        catch (Exception)
        {
            // Showing the message failed too; the log has the details.
        }
    }
}

internal static class AppInfo
{
    /// <summary>The marketing version from the VERSION file, e.g. 0.1.0.</summary>
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

    /// <summary>The build number (the commit count the build was made from); 0 for a build made outside tools\build.ps1.</summary>
    public static int Build { get; } = ReadBuild();

    /// <summary>What to show a person: "0.1.0 (build 4)".</summary>
    public static string Display => Build > 0 ? $"{Version} (build {Build})" : Version;

    /// <summary>
    /// Whether this is a build made for handing out (tools\build.ps1 package).
    /// Only such a build looks for updates: one made for trying things must
    /// not be replaced by a release.
    /// </summary>
    public static bool IsPackaged { get; } = typeof(AppInfo).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
        .OfType<System.Reflection.AssemblyMetadataAttribute>()
        .Any(a => a.Key == "ToolboxPackaged" && a.Value == "true");

    private static int ReadBuild()
    {
        var file = typeof(AppInfo).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyFileVersionAttribute), false)
            .OfType<System.Reflection.AssemblyFileVersionAttribute>().FirstOrDefault()?.Version;
        return System.Version.TryParse(file, out var parsed) && parsed.Revision > 0 ? parsed.Revision : 0;
    }
}
