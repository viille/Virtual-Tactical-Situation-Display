using System.Windows;
using System.Windows.Threading;
using TacticalDisplay.App.Services;

namespace TacticalDisplay.App;

public partial class App : Application
{
    private MainWindow? _mainWindow;

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

        if (!string.IsNullOrWhiteSpace(startupCrashReportMessage))
        {
            MessageBox.Show(
                _mainWindow,
                startupCrashReportMessage,
                "Crash log recovered",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
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
