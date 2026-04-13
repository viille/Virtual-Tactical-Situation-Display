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
    private int _updateCheckStarted;
    private double _cachedSettingsPanelWidth = 320;
    private bool _isClosing;
    private bool _shutdownCompleted;

    public MainWindow()
    {
        InitializeComponent();
        var displayVersion = GetDisplayVersion();
        Title = $"Tactical Situation Display | ver {displayVersion}";
        DataContext = _viewModel;
        _viewModel.AppVersionText = $"ver {displayVersion}";
        RestoreWindowSize();
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        ScopeControl.TargetClicked += OnScopeTargetClicked;
        ScopeControl.LabelMoved += OnScopeLabelMoved;
        Topmost = _viewModel.IsAlwaysOnTop;
        ApplyLayoutState(resizeWindow: false);
        Loaded += OnLoaded;
        Closing += OnClosingAsync;
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
                $"Latest: {result.LatestTag}\n\n" +
                "Download and install it now?";

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

    private void OnCloseButtonClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnScopeTargetClicked(object? sender, ScopeTargetClickEventArgs e)
    {
        if (e.Button == MouseButton.Left)
        {
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
                await Dispatcher.InvokeAsync(Close, DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                DataSourceDebugLog.Info("App", $"Final window close failed | {ex}");
            }
        }
    }
}
