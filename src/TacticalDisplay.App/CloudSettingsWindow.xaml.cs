using System.Diagnostics;
using System.ComponentModel;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using TacticalDisplay.App.Cloud;
using TacticalDisplay.App.Storage;

namespace TacticalDisplay.App;

public partial class CloudSettingsWindow : Window
{
    private readonly CloudOptions _options; private readonly CloudContentStore _content; private readonly CloudSettingsViewModel _viewModel;
    private CancellationTokenSource _operation = new();
    private bool _settingsSaved; private bool _savingSettings;
    public CloudSettingsWindow()
    {
        InitializeComponent();
        var services = CloudBootstrapper.Provider;
        _options = services.GetRequiredService<CloudOptions>();
        _content = services.GetRequiredService<CloudContentStore>();
        _viewModel = services.GetRequiredService<CloudSettingsViewModel>();
        DataContext = _viewModel; Loaded += async (_, _) => await _viewModel.InitializeAsync(_operation.Token);
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _operation.Cancel();
            _operation.Dispose();
            _viewModel.Dispose();
        };
    }
    private async void OnSignIn(object sender, RoutedEventArgs e) { ResetCancellation(); await _viewModel.SignInAsync(OpenBrowser, _operation.Token); }
    private void OnCancelLogin(object sender, RoutedEventArgs e) => _operation.Cancel();
    private async void OnSync(object sender, RoutedEventArgs e) { ResetCancellation(); await _viewModel.SyncAsync(_operation.Token); }
    private async void OnSignOut(object sender, RoutedEventArgs e) { ResetCancellation(); await _viewModel.SignOutAsync(_operation.Token); }
    private async void OnRedeem(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.IsSignedIn)
        {
            if (MessageBox.Show(this, "Sign in with VATSIM before redeeming a share code. Sign in now?",
                    "VTSD Cloud", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            ResetCancellation();
            await _viewModel.SignInAsync(OpenBrowser, _operation.Token);
            if (!_viewModel.IsSignedIn) return;
        }
        var dialog = new InputDialog("Redeem share code", "Share code:") { Owner = this };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Value)) return;
        ResetCancellation();
        var collection = await _viewModel.RedeemAsync(dialog.Value, _operation.Token);
        if (collection is not null && !_viewModel.AutoSyncEnabled && MessageBox.Show(this,
                $"Sync '{collection.Name}' for offline use now?", "VTSD Cloud", MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes)
            await _viewModel.SyncCollectionAsync(collection, _operation.Token);
    }
    private void OnDashboard(object sender, RoutedEventArgs e) => OpenBrowser(_options.DashboardUri);
    private void OnManageCollection(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedCollection is { } collection)
            OpenBrowser(new Uri(_options.DashboardUri, $"collections/{Uri.EscapeDataString(collection.Slug)}"));
    }
    private async void OnOpenKneepad(object sender, RoutedEventArgs e)
    {
        var collection = _viewModel.SelectedCollection; if (collection is null) return;
        if (!collection.ShowKneepadPages) return;
        var pages = _content.GetPages(collection.Slug);
        await _viewModel.TrackKneepadOpenedAsync(CancellationToken.None);
        new KneepadLibraryWindow(collection.Name, pages) { Owner = this }.Show();
    }
    private static void OpenBrowser(Uri uri) => Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
    private void ResetCancellation() { if (!_operation.IsCancellationRequested) return; _operation.Dispose(); _operation = new CancellationTokenSource(); }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_settingsSaved) return;
        if (_savingSettings) { e.Cancel = true; return; }
        e.Cancel = true;
        _savingSettings = true;
        try
        {
            await _viewModel.SaveSettingsAsync();
            _settingsSaved = true;
            _ = Dispatcher.BeginInvoke(Close);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Cloud settings could not be saved.\n\n{ex.Message}", "VTSD Cloud", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _savingSettings = false; }
    }
}
