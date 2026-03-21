using System.Windows;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Input;
using TacticalDisplay.App.Controls;
using TacticalDisplay.App.ViewModels;

namespace TacticalDisplay.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        ScopeControl.TargetClicked += OnScopeTargetClicked;
        Topmost = _viewModel.IsAlwaysOnTop;
        ApplyLayoutState();
        Closing += async (_, _) => await _viewModel.DisposeAsync();
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
    }

    private void ApplyLayoutState()
    {
        var showSettings = _viewModel.ShowSettings;
        SettingsColumn.Width = showSettings ? new GridLength(1.2, GridUnitType.Star) : new GridLength(0);
        ScopeColumn.Width = showSettings ? new GridLength(3, GridUnitType.Star) : new GridLength(1, GridUnitType.Star);
        ScopeBorder.Margin = showSettings ? new Thickness(0, 0, 10, 0) : new Thickness(0);
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
}
