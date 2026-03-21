using System.Windows;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Input;
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
        DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        ScopeControl.TargetClicked += OnScopeTargetClicked;
        Topmost = _viewModel.IsAlwaysOnTop;
        ApplyLayoutState(resizeWindow: false);
        Loaded += OnLoaded;
        Closing += OnClosingAsync;
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
                "Open GitHub Releases page?";

            if (MessageBox.Show(
                    this,
                    message,
                    "Update Available",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information) == MessageBoxResult.Yes)
            {
                UpdateCheckService.OpenReleasesPage(result.ReleaseUri);
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
    }

    private void ApplyLayoutState(bool resizeWindow)
    {
        var showSettings = _viewModel.ShowSettings;
        var measuredPanelWidth = SettingsBorder.ActualWidth;
        if (measuredPanelWidth > 1)
        {
            _cachedSettingsPanelWidth = measuredPanelWidth;
        }

        if (resizeWindow)
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

    private void OnScopeTargetClicked(object? sender, ScopeTargetClickEventArgs e)
    {
        if (e.Button == MouseButton.Left)
        {
            _viewModel.CycleTargetAffiliation(e.TargetId);
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

    private async void OnClosingAsync(object? sender, CancelEventArgs e)
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

        try
        {
            await _viewModel.DisposeAsync();
            _shutdownCompleted = true;
            Closing -= OnClosingAsync;
            Close();
        }
        finally
        {
            _isClosing = false;
        }
    }
}
