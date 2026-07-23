using System.Windows;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using TacticalDisplay.App.Controls;
using TacticalDisplay.App.Services;
using TacticalDisplay.App.ViewModels;
using TacticalDisplay.App.Storage;
using CloudMapFeature = TacticalDisplay.App.Cloud.MapFeature;
using CloudOptions = TacticalDisplay.App.Cloud.CloudOptions;
using Microsoft.Extensions.DependencyInjection;
using CloudStartupService = TacticalDisplay.App.Cloud.CloudStartupService;
using TacticalDisplay.Core.Models;

namespace TacticalDisplay.App;

public partial class MainWindow : Window
{
    private const double MinWidthWithSettings = 980;
    private const double MinWidthWithoutSettings = 640;
    private const double ScopeSettingsGapWidth = 10;
    private const int MaxCachedKneepadWebViews = 6;
    private static readonly MarkdownPipeline CloudKneepadMarkdownPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    private readonly MainViewModel _viewModel = new();
    private readonly UpdateCheckService _updateCheckService = new();
    private readonly TelemetryService _telemetryService = new();
    private readonly GlobalHotkeyService _hotkeyService;
    private WebDisplayServer? _webDisplayServer;
    private int _updateCheckStarted;
    private bool _isClosing;
    private bool _shutdownCompleted;
    private bool _isFullscreen;
    private Rect? _windowedBounds;
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
        await LoadCloudOverlaysAsync();
        _ = RefreshCloudOverlaysAfterStartupAsync();
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
        else if (e.PropertyName == nameof(MainViewModel.ShowKneepad) && _viewModel.ShowKneepad)
        {
            _ = LoadCloudOverlaysAsync();
        }
        else if (e.PropertyName is nameof(MainViewModel.KneepadUrl) or
                 nameof(MainViewModel.ShowKneepadUrl) or
                 nameof(MainViewModel.SelectedKneepadContentMode))
        {
            _ = UpdateKneepadWebViewsAsync();
        }
        else if (e.PropertyName is nameof(MainViewModel.SelectedCloudKneepadPage) or
                 nameof(MainViewModel.ShowCloudKneepad))
        {
            _ = UpdateCloudKneepadViewerAsync();
        }
    }

    private void ExecuteHotkeyAction(string action)
    {
        if (string.Equals(action.Trim(), "fullscreen", StringComparison.OrdinalIgnoreCase))
        {
            ToggleFullscreen();
            return;
        }

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

    private async void OnOpenCloudClick(object sender, RoutedEventArgs e)
    {
        try
        {
            new CloudSettingsWindow { Owner = this }.ShowDialog();
            await LoadCloudOverlaysAsync();
        }
        catch (Exception ex)
        {
            DataSourceDebugLog.Warn("Cloud", $"Cloud settings could not be opened | {ex}");
            MessageBox.Show(this, ex.Message, "VTSD Cloud", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task LoadCloudOverlaysAsync()
    {
        try
        {
            var services = TacticalDisplay.App.Cloud.CloudBootstrapper.Provider;
            var content = services.GetRequiredService<TacticalDisplay.App.Cloud.CloudContentStore>();
            var collectionsService = services.GetRequiredService<TacticalDisplay.App.Cloud.CollectionService>();
            var options = services.GetRequiredService<CloudOptions>();
            if (!content.IsInitialized) await content.LoadAuthorizedCacheAsync();
            var collections = content.Collections;
            new CloudOverlaySettingsStore(System.IO.Path.Combine(AppDataPaths.ApplicationDataDirectory, "cloud-overlays.json")).Apply(collections);
            await SyncActiveCloudKneepadCollectionsAsync(collections, collectionsService);
            var enabledTypes = new CloudPreferencesStore(System.IO.Path.Combine(AppDataPaths.ApplicationDataDirectory, "cloud-settings.json")).Load().EnabledFeatureTypes;
            var features = new List<CloudMapFeature>();
            foreach (var collection in collections.Where(item => item.ShowMapFeaturesOnRadar))
                features.AddRange(content.GetMapFeatures(collection.Slug).Where(feature => enabledTypes.Contains(feature.FeatureType)));
            ScopeControl.CloudMapFeatures = features;
            var activeCloudCollections = collections.Any(item => item.IsActive && item.ShowKneepadPages);
            _viewModel.SetCloudKneepadPages(BuildCloudKneepadPages(collections, content, options.DashboardUri), activeCloudCollections, options.DashboardUri);
            await UpdateCloudKneepadViewerAsync();
        }
        catch (Exception ex)
        {
            DataSourceDebugLog.Warn("Cloud", $"Cached Cloud overlays could not be loaded | {ex.Message}");
        }
    }

    private static async Task SyncActiveCloudKneepadCollectionsAsync(
        IEnumerable<TacticalDisplay.App.Cloud.Collection> collections,
        TacticalDisplay.App.Cloud.CollectionService collectionsService)
    {
        foreach (var collection in collections.Where(item => item.IsActive && item.ShowKneepadPages))
        {
            try
            {
                await collectionsService.SyncAsync(collection, CancellationToken.None);
            }
            catch (TacticalDisplay.App.Cloud.CloudApiException ex)
            {
                DataSourceDebugLog.Warn("Cloud", $"Active Cloud kneepad collection could not be refreshed; using cached content if available | collection={collection.Slug} error={ex.Message}");
            }
        }
    }

    private static IEnumerable<CloudKneepadPageViewModel> BuildCloudKneepadPages(
        IEnumerable<TacticalDisplay.App.Cloud.Collection> collections,
        TacticalDisplay.App.Cloud.CloudContentStore content,
        Uri dashboardUri)
    {
        foreach (var collection in collections
                     .Where(item => item.IsActive && item.ShowKneepadPages)
                     .OrderBy(item => item.AccessSource)
                     .ThenBy(item => item.Name))
        {
            var collectionDashboardUri = new Uri(dashboardUri, $"collections/{Uri.EscapeDataString(collection.Slug)}");
            foreach (var page in content.GetPages(collection.Slug)
                         .OrderBy(item => item.Category)
                         .ThenBy(item => item.OrderIndex)
                         .ThenBy(item => item.Title))
            {
                var title = string.IsNullOrWhiteSpace(page.Title) ? page.Slug : page.Title;
                yield return new CloudKneepadPageViewModel(
                    $"{collection.Slug}:{page.Slug}",
                    collection.Slug,
                    collection.Name,
                    title,
                    page.Category,
                    page.ContentMarkdown,
                    collectionDashboardUri);
            }
        }
    }

    private Task UpdateCloudKneepadViewerAsync()
    {
        var page = _viewModel.SelectedCloudKneepadPage;
        if (!_viewModel.ShowCloudKneepad)
        {
            return Task.CompletedTask;
        }

        try
        {
            if (page is null)
            {
                CloudKneepadViewer.Document = CreateCloudKneepadDocument(null);
                return Task.CompletedTask;
            }

            CloudKneepadViewer.Document = CreateCloudKneepadDocument(page);
        }
        catch (Exception ex)
        {
            DataSourceDebugLog.Warn("Cloud", $"Cloud kneepad page could not be rendered | {ex.Message}");
            CloudKneepadViewer.Document = CreateCloudKneepadDocument(new CloudKneepadPageViewModel(
                "render-error", string.Empty, "VTSD Cloud", "Render error", null,
                $"Cloud kneepad page could not be rendered.\n\n{ex.Message}", _viewModel.CloudDashboardUri ?? new Uri("https://www.vtsd.app/dashboard/")));
        }

        return Task.CompletedTask;
    }

    private static FlowDocument CreateCloudKneepadDocument(CloudKneepadPageViewModel? page)
    {
        var document = new FlowDocument
        {
            Background = new SolidColorBrush(Color.FromRgb(0x10, 0x20, 0x28)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xD9, 0xF2, 0xEC)),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 16,
            PagePadding = new Thickness(0),
            ColumnWidth = double.PositiveInfinity
        };

        if (page is null)
        {
            document.Blocks.Add(new Paragraph(new Run("No synced kneepad pages for the active collections."))
            {
                Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0xA5, 0xAD))
            });
            return document;
        }

        document.Blocks.Add(new Paragraph(new Run(page.Title))
        {
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xFA, 0xD7)),
            Margin = new Thickness(0, 0, 0, 2)
        });
        document.Blocks.Add(new Paragraph(new Run(page.CollectionName))
        {
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0xA5, 0xAD)),
            Margin = new Thickness(0, 0, 0, 16)
        });

        var markdown = Markdown.Parse(page.ContentMarkdown ?? string.Empty);
        foreach (var block in markdown)
        {
            AddMarkdownBlock(document.Blocks, block);
        }

        return document;
    }

    private static void AddMarkdownBlock(BlockCollection blocks, Markdig.Syntax.Block block)
    {
        switch (block)
        {
            case HeadingBlock heading:
                blocks.Add(CreateParagraph(heading.Inline, fontSize: heading.Level <= 1 ? 22 : heading.Level == 2 ? 19 : 17,
                    fontWeight: FontWeights.SemiBold, foreground: Color.FromRgb(0x9A, 0xFA, 0xD7)));
                break;
            case ParagraphBlock paragraph:
                blocks.Add(CreateParagraph(paragraph.Inline));
                break;
            case ListBlock list:
                blocks.Add(CreateList(list));
                break;
            case FencedCodeBlock fenced:
                blocks.Add(CreateCodeBlock(GetLeafBlockText(fenced)));
                break;
            case CodeBlock code:
                blocks.Add(CreateCodeBlock(GetLeafBlockText(code)));
                break;
            case QuoteBlock quote:
                var section = new Section { Margin = new Thickness(12, 4, 0, 8), BorderBrush = new SolidColorBrush(Color.FromRgb(0x35, 0x5A, 0x56)), BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 0, 0, 0) };
                foreach (var child in quote) AddMarkdownBlock(section.Blocks, child);
                blocks.Add(section);
                break;
            case ThematicBreakBlock:
                blocks.Add(new Paragraph(new Run(new string('-', 24))) { Foreground = new SolidColorBrush(Color.FromRgb(0x61, 0x74, 0x80)) });
                break;
        }
    }

    private static List CreateList(ListBlock listBlock)
    {
        var list = new List { MarkerStyle = listBlock.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc, Margin = new Thickness(18, 0, 0, 8) };
        foreach (var item in listBlock.OfType<ListItemBlock>())
        {
            var listItem = new ListItem();
            foreach (var child in item) AddMarkdownBlock(listItem.Blocks, child);
            list.ListItems.Add(listItem);
        }

        return list;
    }

    private static Paragraph CreateParagraph(ContainerInline? inline, double fontSize = 16, FontWeight? fontWeight = null, Color? foreground = null)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 10), FontSize = fontSize };
        if (fontWeight.HasValue) paragraph.FontWeight = fontWeight.Value;
        if (foreground.HasValue) paragraph.Foreground = new SolidColorBrush(foreground.Value);
        AddMarkdownInlines(paragraph.Inlines, inline);
        return paragraph;
    }

    private static Paragraph CreateCodeBlock(string text) => new(new Run(text.TrimEnd()))
    {
        FontFamily = new FontFamily("Consolas"),
        FontSize = 14,
        Background = new SolidColorBrush(Color.FromRgb(0x07, 0x10, 0x15)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x35, 0x5A, 0x56)),
        BorderThickness = new Thickness(1),
        Padding = new Thickness(8),
        Margin = new Thickness(0, 0, 0, 10)
    };

    private static void AddMarkdownInlines(InlineCollection target, ContainerInline? inline)
    {
        if (inline is null) return;
        foreach (var child in inline)
        {
            switch (child)
            {
                case LiteralInline literal:
                    target.Add(new Run(literal.Content.ToString()));
                    break;
                case LineBreakInline:
                    target.Add(new LineBreak());
                    break;
                case CodeInline code:
                    target.Add(new Run(code.Content) { FontFamily = new FontFamily("Consolas"), Background = new SolidColorBrush(Color.FromRgb(0x07, 0x10, 0x15)) });
                    break;
                case EmphasisInline emphasis:
                    Span span = emphasis.DelimiterCount >= 2 ? new Bold() : new Italic();
                    AddMarkdownInlines(span.Inlines, emphasis);
                    target.Add(span);
                    break;
                case LinkInline link:
                    var linkSpan = new Span { Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xFA, 0xD7)) };
                    AddMarkdownInlines(linkSpan.Inlines, link);
                    target.Add(linkSpan);
                    break;
                case ContainerInline container:
                    AddMarkdownInlines(target, container);
                    break;
            }
        }
    }

    private static string GetLeafBlockText(LeafBlock block)
    {
        var lines = block.Lines.Lines;
        if (lines is null) return string.Empty;
        return string.Join(Environment.NewLine, lines.Select(line => line.Slice.ToString()));
    }

    private async Task RefreshCloudOverlaysAfterStartupAsync()
    {
        try
        {
            await TacticalDisplay.App.Cloud.CloudBootstrapper.Provider.GetRequiredService<CloudStartupService>()
                .InitializeAsync(CancellationToken.None);
            await LoadCloudOverlaysAsync();
        }
        catch (Exception ex) { DataSourceDebugLog.Warn("Cloud", $"Cloud startup overlay refresh failed | {ex.Message}"); }
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

    private void OnCloudKneepadDashboardClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.CloudDashboardUri is null)
        {
            return;
        }

        Process.Start(new ProcessStartInfo { FileName = _viewModel.CloudDashboardUri.AbsoluteUri, UseShellExecute = true });
    }

    private async Task InitializeKneepadWebViewsAsync()
    {
        if (_kneepadWebViewEnvironment is not null || _kneepadWebViewsInitializing)
        {
            await UpdateKneepadWebViewsAsync();
            await UpdateCloudKneepadViewerAsync();
            return;
        }

        _kneepadWebViewsInitializing = true;
        try
        {
            _kneepadWebViewEnvironment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: AppDataPaths.WebViewUserDataDirectory);
            await UpdateKneepadWebViewsAsync();
            await UpdateCloudKneepadViewerAsync();
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
        if (_isFullscreen || WindowState != WindowState.Normal)
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
        ExitFullscreen();
    }

    private void OnFullscreenButtonClick(object sender, RoutedEventArgs e)
    {
        ToggleFullscreen();
    }

    private void ToggleFullscreen()
    {
        if (_isFullscreen)
        {
            ExitFullscreen();
            return;
        }

        EnterFullscreen();
    }

    private void EnterFullscreen()
    {
        if (_isFullscreen)
        {
            return;
        }

        if (WindowState == WindowState.Normal)
        {
            _windowedBounds = new Rect(Left, Top, ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height);
        }
        else
        {
            _windowedBounds = RestoreBounds;
        }

        var monitorBounds = GetCurrentMonitorBoundsInDips();
        WindowState = WindowState.Normal;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Left = monitorBounds.Left;
        Top = monitorBounds.Top;
        Width = monitorBounds.Width;
        Height = monitorBounds.Height;
        _isFullscreen = true;
        ExitFullscreenButton.Visibility = Visibility.Visible;
        FullscreenButton.Visibility = Visibility.Collapsed;
    }

    private void ExitFullscreen()
    {
        if (!_isFullscreen)
        {
            if (WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
            }

            return;
        }

        _isFullscreen = false;
        ResizeMode = ResizeMode.CanResize;
        WindowStyle = WindowStyle.None;
        WindowState = WindowState.Normal;
        if (_windowedBounds is Rect bounds && bounds.Width > 0 && bounds.Height > 0)
        {
            Left = bounds.Left;
            Top = bounds.Top;
            Width = bounds.Width;
            Height = bounds.Height;
        }

        _windowedBounds = null;
        ExitFullscreenButton.Visibility = Visibility.Collapsed;
        FullscreenButton.Visibility = Visibility.Visible;
    }

    private Rect GetCurrentMonitorBoundsInDips()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return SystemParameters.WorkArea;
        }

        var info = new MonitorInfo { CbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return SystemParameters.WorkArea;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        return new Rect(
            info.Monitor.Left / dpi.DpiScaleX,
            info.Monitor.Top / dpi.DpiScaleY,
            (info.Monitor.Right - info.Monitor.Left) / dpi.DpiScaleX,
            (info.Monitor.Bottom - info.Monitor.Top) / dpi.DpiScaleY);
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (!_isFullscreen && WindowState == WindowState.Maximized)
        {
            // A double-click on the custom frame can still maximize the window.
            // Treat it as the same monitor-local fullscreen mode.
            Dispatcher.BeginInvoke(ToggleFullscreen, DispatcherPriority.Loaded);
        }
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || (!_isFullscreen && WindowState != WindowState.Maximized))
        {
            return;
        }

        ExitFullscreen();
        e.Handled = true;
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
            await TrackCloudClosedAsync();
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

            await TrackCloudClosedAsync();
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

    private static async Task TrackCloudClosedAsync()
    {
        try
        {
            await TacticalDisplay.App.Cloud.CloudBootstrapper.Provider.GetRequiredService<CloudStartupService>()
                .TrackClosedAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            DataSourceDebugLog.Debug("Cloud", "Cloud close telemetry timed out; shutdown continues");
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

    private const uint MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int CbSize;
        public MonitorRect Monitor;
        public MonitorRect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
