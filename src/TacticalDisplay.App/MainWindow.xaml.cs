using System.Windows;
using System.ComponentModel;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TacticalDisplay.App.Controls;
using TacticalDisplay.App.Services;
using TacticalDisplay.App.ViewModels;

namespace TacticalDisplay.App;

public partial class MainWindow : Window
{
    private const double MinWidthWithSettings = 980;
    private const double MinWidthWithoutSettings = 640;
    private const double ScopeSettingsGapWidth = 10;

    private readonly MainViewModel _viewModel = new();
    private readonly UpdateCheckService _updateCheckService = new();
    private WebDisplayServer? _webDisplayServer;
    private int _updateCheckStarted;
    private double _cachedSettingsPanelWidth = 320;
    private bool _isClosing;
    private bool _shutdownCompleted;
    private bool _fullscreenWarningOpen;

    public MainWindow()
    {
        InitializeComponent();
        var displayVersion = GetDisplayVersion();
        Title = $"Tactical Situation Display | ver {displayVersion}";
        DataContext = _viewModel;
        _viewModel.AppVersionText = $"ver {displayVersion}";
        ApplyWebDisplayServerState();
        RestoreWindowSize();
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        ScopeControl.TargetClicked += OnScopeTargetClicked;
        ScopeControl.LabelMoved += OnScopeLabelMoved;
        Topmost = _viewModel.IsAlwaysOnTop;
        ApplyLayoutState(resizeWindow: false);
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
                    UpdateCheckService.OpenReleasesPage(result.ReleaseUri);
                    return;
                }

                try
                {
                    if (await _updateCheckService.DownloadAndStartUpdateAsync(result, CancellationToken.None))
                    {
                        Close();
                    }
                    else
                    {
                        UpdateCheckService.OpenReleasesPage(result.ReleaseUri);
                    }
                }
                catch
                {
                    UpdateCheckService.OpenReleasesPage(result.ReleaseUri);
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
            ApplyLayoutState(resizeWindow: true);
        }
        else if (e.PropertyName == nameof(MainViewModel.Settings))
        {
            MapControl.RefreshMapState();
        }
        else if (e.PropertyName == nameof(MainViewModel.WebServerEnabled))
        {
            ApplyWebDisplayServerState();
        }
    }

    private void ApplyLayoutState(bool resizeWindow)
    {
        var showSettings = _viewModel.ShowSettings;
        var measuredPanelWidth = SettingsBorder.ActualWidth;
        if (measuredPanelWidth > 1)
        {
            _cachedSettingsPanelWidth = measuredPanelWidth;
        }

        if (resizeWindow && WindowState == WindowState.Normal)
        {
            var widthDelta = _cachedSettingsPanelWidth + ScopeSettingsGapWidth;
            if (showSettings)
            {
                MinWidth = MinWidthWithSettings;
                Width = System.Math.Max(Width + widthDelta, MinWidthWithSettings);
            }
            else
            {
                MinWidth = MinWidthWithoutSettings;
                Width = System.Math.Max(Width - widthDelta, MinWidthWithoutSettings);
            }
        }
        else
        {
            MinWidth = showSettings ? MinWidthWithSettings : MinWidthWithoutSettings;
        }

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
