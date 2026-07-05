using System.Windows;
using System.Windows.Threading;
using TacticalDisplay.App.Services;
using TacticalDisplay.App.Cloud;
using Microsoft.Extensions.DependencyInjection;

namespace TacticalDisplay.App;

public partial class App : Application
{
    private MainWindow? _mainWindow;
    private readonly CancellationTokenSource _cloudStartupCancellation = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        LocalDumpConfigurator.EnsureLocalDumpsConfigured();
        if (AppWatchdogLauncher.TryRelaunchUnderWatchdog(e.Args))
        {
            Shutdown(0);
            return;
        }

        var startupCrashReportMessage = WindowsErrorReportCollector.LogExistingReports();

        base.OnStartup(e);
        _mainWindow = new MainWindow();
        _mainWindow.Show();
        var cloudStartup = CloudBootstrapper.Provider.GetRequiredService<CloudStartupService>()
            .InitializeAsync(_cloudStartupCancellation.Token);
        _ = cloudStartup.ContinueWith(task =>
            DataSourceDebugLog.Warn("Cloud", $"Cloud startup failed | {task.Exception?.GetBaseException().Message}"),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

        if (!string.IsNullOrWhiteSpace(startupCrashReportMessage))
        {
            var message =
                startupCrashReportMessage +
                $"{Environment.NewLine}{Environment.NewLine}" +
                "Please send a debug report so this crash can be investigated. Open Settings > Debug > Send Debug Report, keep logs selected, review the raw package, and confirm the upload.";
            MessageBox.Show(
                _mainWindow,
                message,
                "Crash log recovered",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _cloudStartupCancellation.Cancel();
        _cloudStartupCancellation.Dispose();
        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        DataSourceDebugLog.Crash("App.Dispatcher", "Unhandled UI exception", e.Exception);
        e.Handled = true;
        Current.Shutdown(1);
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception;
        DataSourceDebugLog.Crash(
            "AppDomain",
            $"Unhandled app-domain exception | terminating={e.IsTerminating}",
            exception);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        DataSourceDebugLog.Crash("TaskScheduler", "Unobserved task exception", e.Exception);
        e.SetObserved();
    }
}
