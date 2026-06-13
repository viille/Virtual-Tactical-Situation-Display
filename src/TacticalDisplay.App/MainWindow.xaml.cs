using System.Windows;
using System.ComponentModel;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using TacticalDisplay.App.Controls;
using TacticalDisplay.App.Services;
using TacticalDisplay.App.ViewModels;
using TacticalDisplay.Core.Models;

namespace TacticalDisplay.App;

public partial class MainWindow : Window
{
    private const double MinWidthWithSettings = 980;
    private const double MinWidthWithoutSettings = 640;
    private const double ScopeSettingsGapWidth = 10;
    private const int MaxCachedKneepadWebViews = 6;

    private readonly MainViewModel _viewModel = new();
    private readonly UpdateCheckService _updateCheckService = new();
    private readonly TelemetryService _telemetryService = new();
    private readonly GlobalHotkeyService _hotkeyService;
    private WebDisplayServer? _webDisplayServer;
    private int _updateCheckStarted;
    private bool _isClosing;
    private bool _shutdownCompleted;
    private bool _fullscreenWarningOpen;
    private bool _kneepadWebViewsInitializing;
    private CoreWebView2Environment? _kneepadWebViewEnvironment;
    private readonly Dictionary<KneepadPage, WebView2> _kneepadWebViews = new();

    public MainWindow()
    {
        InitializeComponent();
        var displayVersion = GetDisplayVersion();
        Title = $"Tactical Situation Display | ver {displayVersion}";
        DataContext = _viewModel;
        _hotkeyService = new GlobalHotkeyService(Dispatcher, ExecuteHotkeyAction);
        _viewModel.AppVersionText = $"ver {displayVersion}";
        ApplyWebDisplayServerState();
        RestoreWindowSize();
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.HotkeyBindingsChanged += OnHotkeyBindingsChanged;
        ScopeControl.TargetClicked += OnScopeTargetClicked;
        ScopeControl.LabelMoved += OnScopeLabelMoved;
        Topmost = _viewModel.IsAlwaysOnTop;
        ApplyLayoutState();
        Loaded += OnLoaded;
        Closing += OnClosingAsync;
        StateChanged += OnWindowStateChanged;
        KeyDown += OnWindowKeyDown;
    }

    private static string GetDisplayVersion()
    {
        var informationalVersion = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            var separatorIndex = informationalVersion.IndexOfAny(['-', '+']);
            return separatorIndex >= 0 ? informationalVersion[..separatorIndex] : informationalVersion;
        }

        return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
    }

    private void ApplyWebDisplayServerState()
    {
        if (_viewModel.WebServerEnabled)
        {
            StartWebDisplayServer();
            return;
        }

        _ = StopWebDisplayServerAsync();
    }

    private void StartWebDisplayServer()
    {
        if (_webDisplayServer is not null)
        {
            return;
        }

        var server = new WebDisplayServer(_viewModel, Dispatcher);
        if (!server.Start())
        {
            _ = server.DisposeAsync();
            _viewModel.SetWebDisplayStatus($"Web: unavailable (port {WebDisplayServer.DefaultPort})");
            return;
        }

        _webDisplayServer = server;
        var tabletUrl = server.LocalUrls.FirstOrDefault(url => !url.Contains("localhost", StringComparison.OrdinalIgnoreCase))
            ?? server.LocalUrls.FirstOrDefault()
            ?? $"http://localhost:{WebDisplayServer.DefaultPort}/";
        _viewModel.SetWebDisplayStatus($"Web: {tabletUrl}");
    }

    private async Task StopWebDisplayServerAsync()
    {
        var server = _webDisplayServer;
        _webDisplayServer = null;
        if (server is not null)
        {
            await server.DisposeAsync();
        }

        _viewModel.SetWebDisplayStatus("Web: off");
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hotkeyService.Start(_viewModel.Settings.Hotkeys);
        await InitializeKneepadWebViewsAsync();
        PromptForDiagnosticTelemetryConsentIfNeeded();
        _telemetryService.SendStartupTelemetryInBackground(
            GetDisplayVersion(),
            _viewModel.Settings,
            _viewModel.DiagnosticTelemetryEnabled);

        if (Interlocked.Exchange(ref _updateCheckStarted, 1) != 0)
        {
            return;
        }

        try
        {
            var result = await _updateCheckService.CheckForUpdateAsync(CancellationToken.None);
            if (result is null)
            {
                return;
            }

            var message =
                $"A new version is available.\n\n" +
                $"Current: v{result.CurrentVersion}\n" +
                $"Latest: {result.LatestTag}\n\n";

            var releaseNotes = TrimReleaseNotes(result.ReleaseNotes);
            if (!string.IsNullOrWhiteSpace(releaseNotes))
            {
                message += $"Changelog:\n{releaseNotes}\n\n";
            }

            message += "Download and install it now?";

            if (MessageBox.Show(
                    this,
                    message,
                    "Update Available",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information) == MessageBoxResult.Yes)
            {
                if (result.AssetDownloadUri is null)
                {
                    DataSourceDebugLog.Warn("Update", $"Automatic update unavailable; release asset missing | release={result.LatestTag}");
                    OfferManualReleasePage(result.ReleaseUri, "The automatic updater could not find the release executable asset.");
                    return;
                }

                try
                {
                    var progressWindow = new UpdateProgressWindow
                    {
                        Owner = this
                    };
                    var progress = new Progress<UpdateProgress>(progressWindow.Update);
                    progressWindow.Show();
                    if (await _updateCheckService.DownloadAndStartUpdateAsync(result, progress, CancellationToken.None))
                    {
                        await ShutdownForUpdateAsync(progressWindow);
                    }
                    else
                    {
                        progressWindow.Close();
                        OfferManualReleasePage(result.ReleaseUri, "The automatic updater could not install the update.");
                    }
                }
                catch (Exception ex)
                {
                    foreach (Window window in OwnedWindows)
                    {
                        if (window is UpdateProgressWindow)
                        {
                            window.Close();
                            break;
                        }
                    }

                    DataSourceDebugLog.Warn("Update", $"Automatic update failed with exception | release={result.LatestTag} error={ex}");
                    OfferManualReleasePage(result.ReleaseUri, "The automatic updater failed.");
                }
            }
        }
        catch
        {
            // Silent failure: update checks should never block app startup.
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsAlwaysOnTop))
        {
            Topmost = _viewModel.IsAlwaysOnTop;
        }
        else if (e.PropertyName == nameof(MainViewModel.ShowSettings))
        {
            ApplyLayoutState();
        }
        else if (e.PropertyName == nameof(MainViewModel.Settings))
        {
            MapControl.RefreshMapState();
        }
        else if (e.PropertyName == nameof(MainViewModel.WebServerEnabled))
        {
            ApplyWebDisplayServerState();
        }
        else if (e.PropertyName is nameof(MainViewModel.KneepadUrl) or
                 nameof(MainViewModel.ShowKneepadUrl) or
                 nameof(MainViewModel.SelectedKneepadContentMode))
        {
            _ = UpdateKneepadWebViewsAsync();
        }
    }

    private void ExecuteHotkeyAction(string action)
    {
        _viewModel.ExecuteHotkeyAction(action);
    }

    private void OnHotkeyBindingsChanged(object? sender, EventArgs e)
    {
        _hotkeyService.UpdateBindings(_viewModel.Settings.Hotkeys);
    }

    private void OnConfigureHotkeysClick(object sender, RoutedEventArgs e)
    {
        var dialog = new HotkeyConfigDialog(_viewModel, _hotkeyService)
        {
            Owner = this
        };
        dialog.ShowDialog();
    }

    private void PromptForDiagnosticTelemetryConsentIfNeeded()
    {
        if (_viewModel.Settings.DiagnosticTelemetryConsentAsked || _viewModel.SuppressModalDialogs)
        {
            return;
        }

        if (_viewModel.DiagnosticTelemetryEnabled)
        {
            _viewModel.SetDiagnosticTelemetryConsent(enabled: true);
            return;
        }

        var result = MessageBox.Show(
            this,
            "Allow extended telemetry?\n\nVTSD already sends one anonymous daily app_active ping with installation ID and app version. Extended telemetry also includes non-flight diagnostics such as data source mode, map toggles, web display setting, OS version, and kneepad page count.",
            "Telemetry",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        _viewModel.SetDiagnosticTelemetryConsent(result == MessageBoxResult.Yes);
    }

    private void OfferManualReleasePage(Uri releaseUri, string reason)
    {
        if (MessageBox.Show(
                this,
                $"{reason}\n\nOpen the GitHub release page for manual download?",
                "Automatic Update Failed",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            UpdateCheckService.OpenReleasesPage(releaseUri);
        }
    }

    private void OnSendDebugReportClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new DebugReportDialog(
            GetDisplayVersion(),
            _viewModel.Settings,
            _telemetryService)
        {
            Owner = this
        };
        dialog.ShowDialog();
    }

    private async Task InitializeKneepadWebViewsAsync()
    {
        if (_kneepadWebViewEnvironment is not null || _kneepadWebViewsInitializing)
        {
            await UpdateKneepadWebViewsAsync();
            return;
        }

        _kneepadWebViewsInitializing = true;
        try
        {
            _kneepadWebViewEnvironment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: AppDataPaths.WebViewUserDataDirectory);
            await UpdateKneepadWebViewsAsync();
        }
        catch (Exception ex)
        {
            DataSourceDebugLog.Warn("App", $"Kneepad WebView2 initialization failed | {ex}");
        }
        finally
        {
            _kneepadWebViewsInitializing = false;
        }
    }

    private async Task UpdateKneepadWebViewsAsync()
    {
        var environment = _kneepadWebViewEnvironment;
        if (environment is null)
        {
            return;
        }

        var pages = _viewModel.Settings.KneepadPages;
        var livePages = pages.ToHashSet();
        foreach (var stale in _kneepadWebViews.Keys.Where(page => !livePages.Contains(page)).ToList())
        {
            var webView = _kneepadWebViews[stale];
            KneepadWebViewHost.Children.Remove(webView);
            DisposeKneepadWebView(webView);
            _kneepadWebViews.Remove(stale);
        }

        var selectedIndex = pages.Count == 0 ? -1 : System.Math.Clamp(_viewModel.Settings.SelectedKneepadPageIndex, 0, pages.Count - 1);
        var selectedPage = selectedIndex >= 0 ? pages[selectedIndex] : null;
        foreach (var page in pages)
        {
            if (!string.Equals(page.ContentMode, "Url", StringComparison.OrdinalIgnoreCase) ||
                !TryBuildKneepadUri(page.Url, out var pageUri))
            {
                if (_kneepadWebViews.TryGetValue(page, out var oldWebView))
                {
                    oldWebView.Visibility = Visibility.Collapsed;
                }

                continue;
            }

            var webView = await EnsureKneepadPageWebViewAsync(page, environment);
            webView.Visibility = ReferenceEquals(page, selectedPage) && _viewModel.ShowKneepadUrl
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (webView.Source is null ||
                !string.Equals(webView.Source.AbsoluteUri, pageUri!.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
            {
                webView.Source = pageUri;
            }
        }

        TrimKneepadWebViewCache(selectedPage);
    }

    private async Task<WebView2> EnsureKneepadPageWebViewAsync(KneepadPage page, CoreWebView2Environment environment)
    {
        if (_kneepadWebViews.TryGetValue(page, out var webView))
        {
            return webView;
        }

        webView = new WebView2
        {
            Visibility = Visibility.Collapsed
        };
        _kneepadWebViews[page] = webView;
        KneepadWebViewHost.Children.Add(webView);
        await webView.EnsureCoreWebView2Async(environment);
        return webView;
    }

    private void TrimKneepadWebViewCache(KneepadPage? selectedPage)
    {
        if (_kneepadWebViews.Count <= MaxCachedKneepadWebViews)
        {
            return;
        }

        foreach (var page in _kneepadWebViews.Keys
                     .Where(page => !ReferenceEquals(page, selectedPage))
                     .Take(_kneepadWebViews.Count - MaxCachedKneepadWebViews)
                     .ToList())
        {
            var webView = _kneepadWebViews[page];
            KneepadWebViewHost.Children.Remove(webView);
            DisposeKneepadWebView(webView);
            _kneepadWebViews.Remove(page);
        }
    }

    private void DisposeAllKneepadWebViews()
    {
        foreach (var webView in _kneepadWebViews.Values.ToList())
        {
            KneepadWebViewHost.Children.Remove(webView);
            DisposeKneepadWebView(webView);
        }

        _kneepadWebViews.Clear();
    }

    private static void DisposeKneepadWebView(WebView2 webView)
    {
        try
        {
            webView.Dispose();
        }
        catch (Exception ex)
        {
            DataSourceDebugLog.Warn("App", $"Kneepad WebView2 dispose failed | {ex.Message}");
        }
    }

    private static bool TryBuildKneepadUri(string? value, out Uri? uri)
    {
        uri = null;
        var trimmed = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return false;
        }

        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = $"https://{trimmed}";
        }

        return Uri.TryCreate(trimmed, UriKind.Absolute, out uri);
    }

    private void ApplyLayoutState()
    {
        var showSettings = _viewModel.ShowSettings;
        MinWidth = showSettings ? MinWidthWithSettings : MinWidthWithoutSettings;

        SettingsColumn.Width = showSettings ? new GridLength(1.2, GridUnitType.Star) : new GridLength(0);
        ScopeColumn.Width = showSettings ? new GridLength(3, GridUnitType.Star) : new GridLength(1, GridUnitType.Star);
        ScopeBorder.Margin = showSettings ? new Thickness(0, 0, ScopeSettingsGapWidth, 0) : new Thickness(0);
    }

    private void RestoreWindowSize()
    {
        Width = System.Math.Max(_viewModel.Settings.WindowWidth, MinWidth);
        Height = System.Math.Max(_viewModel.Settings.WindowHeight, MinHeight);
    }

    private void StoreWindowSize()
    {
        if (WindowState != WindowState.Normal)
        {
            return;
        }

        _viewModel.Settings.WindowWidth = ActualWidth;
        _viewModel.Settings.WindowHeight = ActualHeight;
    }

    private void OnWindowMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ClickCount != 1)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject source && IsFunctionalArea(source))
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
        }
    }

    private bool IsFunctionalArea(DependencyObject source)
    {
        return HasAncestor<ButtonBase>(source)
            || HasAncestor(source, DisplaySurface)
            || HasAncestor<TacticalScopeControl>(source)
            || HasAncestor<OpenFreeMapControl>(source);
    }

    private static bool HasAncestor(DependencyObject? source, DependencyObject ancestor)
    {
        while (source is not null)
        {
            if (ReferenceEquals(source, ancestor))
            {
                return true;
            }

            source = GetParent(source);
        }

        return false;
    }

    private static bool HasAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T)
            {
                return true;
            }

            source = GetParent(source);
        }

        return false;
    }

    private static DependencyObject? GetParent(DependencyObject source)
    {
        try
        {
            return VisualTreeHelper.GetParent(source) ?? LogicalTreeHelper.GetParent(source);
        }
        catch (InvalidOperationException)
        {
            return LogicalTreeHelper.GetParent(source);
        }
    }

    private void OnMinimizeButtonClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnExitFullscreenButtonClick(object sender, RoutedEventArgs e)
    {
        ExitFullscreenLikeState();
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        var isFullscreenLike = WindowState == WindowState.Maximized;
        ExitFullscreenButton.Visibility = isFullscreenLike ? Visibility.Visible : Visibility.Collapsed;

        if (isFullscreenLike)
        {
            ShowFullscreenWarning();
        }
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || WindowState != WindowState.Maximized)
        {
            return;
        }

        ExitFullscreenLikeState();
        e.Handled = true;
    }

    private void ExitFullscreenLikeState()
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
        }
    }

    private void ShowFullscreenWarning()
    {
        if (_fullscreenWarningOpen)
        {
            return;
        }

        _fullscreenWarningOpen = true;
        try
        {
            MessageBox.Show(
                this,
                "This application is not intended to be used in fullscreen mode.\n\nUse the EXIT button or press Esc to return to windowed mode.",
                "Fullscreen Not Recommended",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _fullscreenWarningOpen = false;
        }
    }

    private void OnHelpButtonClick(object sender, RoutedEventArgs e)
    {
        HelpOverlay.Visibility = HelpOverlay.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void OnHelpOverlayMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        HelpOverlay.Visibility = Visibility.Collapsed;
    }

    private void OnCloseButtonClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnScopeTargetClicked(object? sender, ScopeTargetClickEventArgs e)
    {
        if (e.Button == MouseButton.Left)
        {
            if (_viewModel.TrySelectInterceptTarget(e.TargetId))
            {
                return;
            }

            _viewModel.CycleTargetAffiliation(e.TargetId);
            return;
        }

        if (e.Button == MouseButton.Middle)
        {
            _viewModel.ToggleTargetLabelVisibility(e.TargetId);
            return;
        }

        if (e.Button == MouseButton.Right)
        {
            var dialog = new InputDialog("Rename Target", $"Name for {e.TargetId}:", _viewModel.GetManualName(e.TargetId) ?? string.Empty)
            {
                Owner = this
            };
            if (dialog.ShowDialog() == true)
            {
                _viewModel.SetManualName(e.TargetId, dialog.Value);
            }
        }
    }

    private void OnScopeLabelMoved(object? sender, ScopeLabelMovedEventArgs e)
    {
        _viewModel.SetTargetLabelOffset(e.TargetId, e.OffsetX, e.OffsetY);
    }

    private void OnClosingAsync(object? sender, CancelEventArgs e)
    {
        if (_shutdownCompleted)
        {
            return;
        }

        if (_isClosing)
        {
            e.Cancel = true;
            return;
        }

        e.Cancel = true;
        _isClosing = true;
        _ = CompleteShutdownAsync();
    }

    private async Task CompleteShutdownAsync()
    {
        try
        {
            StoreWindowSize();
            DisposeAllKneepadWebViews();
            _hotkeyService.Dispose();
            if (_webDisplayServer is not null)
            {
                await _webDisplayServer.DisposeAsync();
                _webDisplayServer = null;
            }
            await _viewModel.DisposeAsync();
        }
        catch (Exception ex)
        {
            DataSourceDebugLog.Info("App", $"Shutdown cleanup failed | {ex}");
        }
        finally
        {
            _shutdownCompleted = true;
            Closing -= OnClosingAsync;
            _isClosing = false;
            try
            {
                DataSourceDebugLog.MarkCleanShutdown();
                await Dispatcher.InvokeAsync(Close, DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                DataSourceDebugLog.Info("App", $"Final window close failed | {ex}");
            }
        }
    }

    private async Task ShutdownForUpdateAsync(UpdateProgressWindow progressWindow)
    {
        _isClosing = true;
        _viewModel.SuppressModalDialogs = true;
        progressWindow.Update(new UpdateProgress("Closing current app...", null));
        try
        {
            CloseOwnedWindowsExcept(progressWindow);
            StoreWindowSize();
            DisposeAllKneepadWebViews();
            _hotkeyService.Dispose();
            if (_webDisplayServer is not null)
            {
                await _webDisplayServer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                _webDisplayServer = null;
            }

            await _viewModel.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            DataSourceDebugLog.MarkCleanShutdown();
        }
        catch (TimeoutException ex)
        {
            DataSourceDebugLog.Warn("App", $"Update shutdown timed out; exiting process | {ex.Message}");
        }
        catch (Exception ex)
        {
            DataSourceDebugLog.Warn("App", $"Update shutdown cleanup failed; exiting process | {ex}");
        }
        finally
        {
            _shutdownCompleted = true;
            Closing -= OnClosingAsync;
            try
            {
                progressWindow.Close();
                Application.Current.Shutdown(0);
            }
            finally
            {
                Environment.Exit(0);
            }
        }
    }

    private void CloseOwnedWindowsExcept(Window keepOpen)
    {
        foreach (Window window in OwnedWindows.Cast<Window>().ToList())
        {
            if (ReferenceEquals(window, keepOpen))
            {
                continue;
            }

            try
            {
                window.Close();
            }
            catch (Exception ex)
            {
                DataSourceDebugLog.Warn("App", $"Failed to close owned window during update shutdown | {ex.Message}");
            }
        }
    }

    private static string? TrimReleaseNotes(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return null;
        }

        var normalized = notes.Trim();
        const int maxLength = 1500;
        return normalized.Length <= maxLength
            ? normalized
            : $"{normalized[..maxLength].Trim()}...";
    }
}
