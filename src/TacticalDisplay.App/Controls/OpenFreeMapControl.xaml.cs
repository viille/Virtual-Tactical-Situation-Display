using System.Diagnostics;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using TacticalDisplay.App.Services;
using TacticalDisplay.Core.Models;

namespace TacticalDisplay.App.Controls;

public partial class OpenFreeMapControl : UserControl
{
    private const double MetersPerNauticalMile = 1852.0;
    private const string WebView2RuntimeDownloadUrl = "https://developer.microsoft.com/en-us/microsoft-edge/webview2/";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private bool _webViewReady;
    private bool _initializing;
    private string? _pendingMapStateJson;
    private bool _mapStateInFlight;
    private bool _webMessageHandlerAttached;
    private bool _processFailedHandlerAttached;
    private DateTimeOffset _mapStateSentAt;
    private DateTimeOffset _lastMapRecoveryAt;
    private DateTimeOffset _mapHeartbeatWatchStartedAt;
    private DateTimeOffset _lastMapHeartbeatAt;
    private bool _mapShouldRender;
    private readonly DispatcherTimer _mapWatchdogTimer;

    public static readonly DependencyProperty PictureProperty = DependencyProperty.Register(
        nameof(Picture),
        typeof(TacticalPicture),
        typeof(OpenFreeMapControl),
        new PropertyMetadata(null, OnMapStateChanged));

    public static readonly DependencyProperty SettingsProperty = DependencyProperty.Register(
        nameof(Settings),
        typeof(TacticalDisplaySettings),
        typeof(OpenFreeMapControl),
        new PropertyMetadata(null, OnMapStateChanged));

    public OpenFreeMapControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => UpdateMapState();
        MapWebView.NavigationCompleted += OnNavigationCompleted;
        _mapWatchdogTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _mapWatchdogTimer.Tick += OnMapWatchdogTick;
    }

    public TacticalPicture? Picture
    {
        get => (TacticalPicture?)GetValue(PictureProperty);
        set => SetValue(PictureProperty, value);
    }

    public TacticalDisplaySettings? Settings
    {
        get => (TacticalDisplaySettings?)GetValue(SettingsProperty);
        set => SetValue(SettingsProperty, value);
    }

    public void RefreshMapState() => UpdateMapState();

    private static void OnMapStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((OpenFreeMapControl)d).UpdateMapState();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _mapWatchdogTimer.Start();
        AttachWebViewHandlers();
        if (_webViewReady || _initializing)
        {
            UpdateMapState();
            return;
        }

        _initializing = true;
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: ResolveWebViewUserDataFolder());
            await MapWebView.EnsureCoreWebView2Async(environment);
            MapWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            MapWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            AttachWebViewHandlers();
            MapWebView.NavigateToString(CreateMapHtml());
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            StatusText.Text = $"Map unavailable: WebView2 Runtime is not installed ({ex.HResult:X8})";
            StatusText.Visibility = Visibility.Visible;
            ShowWebViewRuntimeMissingDialog();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Map unavailable: {ex.Message} ({ex.HResult:X8})";
            StatusText.Visibility = Visibility.Visible;
        }
        finally
        {
            _initializing = false;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachWebViewHandlers();

        _pendingMapStateJson = null;
        _mapStateInFlight = false;
        _mapWatchdogTimer.Stop();
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _webViewReady = e.IsSuccess;
        _mapHeartbeatWatchStartedAt = e.IsSuccess ? DateTimeOffset.UtcNow : DateTimeOffset.MinValue;
        _lastMapHeartbeatAt = DateTimeOffset.MinValue;
        StatusText.Visibility = e.IsSuccess ? Visibility.Collapsed : Visibility.Visible;
        if (!e.IsSuccess)
        {
            StatusText.Text = $"Map unavailable: {e.WebErrorStatus}";
            return;
        }

        UpdateMapState();
        SendPendingMapState();
    }

    private void UpdateMapState()
    {
        var settings = Settings;
        var picture = Picture;
        var showMap = settings?.ShowMapLayer == true && picture is not null;
        _mapShouldRender = showMap;
        Opacity = showMap ? Math.Clamp(settings!.MapOpacity, 0.0, 1.0) : 0.0;
        Visibility = showMap ? Visibility.Visible : Visibility.Collapsed;

        if (!showMap || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var ownship = picture!.Ownship;
        var headingUp = settings!.OrientationMode == ScopeOrientationMode.HeadingUp;
        var state = new MapState(
            ownship.LatitudeDeg,
            ownship.LongitudeDeg,
            settings.SelectedRangeNm,
            headingUp ? ownship.HeadingDeg : 0.0,
            MapboxDefaults.ResolveAccessToken(),
            MapboxDefaults.ResolveDisplayStyleUrl(settings.ShowControlledAirspaceLayer));

        var json = JsonSerializer.Serialize(state, JsonOptions);
        QueueMapState(json);
    }

    private void QueueMapState(string json)
    {
        _pendingMapStateJson = json;
        if (_webViewReady && !_mapStateInFlight)
        {
            SendPendingMapState();
        }
    }

    private void SendPendingMapState()
    {
        if (!_webViewReady ||
            _mapStateInFlight ||
            MapWebView.CoreWebView2 is null ||
            _pendingMapStateJson is null)
        {
            return;
        }

        var json = _pendingMapStateJson;
        _pendingMapStateJson = null;
        _mapStateInFlight = true;
        _mapStateSentAt = DateTimeOffset.UtcNow;
        try
        {
            MapWebView.CoreWebView2.PostWebMessageAsJson(json);
        }
        catch (InvalidOperationException)
        {
            _pendingMapStateJson ??= json;
            _mapStateInFlight = false;
        }
        catch (ArgumentException)
        {
            _pendingMapStateJson ??= json;
            _mapStateInFlight = false;
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var message = e.TryGetWebMessageAsString();
        if (string.Equals(message, "map-heartbeat", StringComparison.Ordinal))
        {
            _lastMapHeartbeatAt = DateTimeOffset.UtcNow;
            return;
        }

        if (string.Equals(message, "map-webgl-context-lost", StringComparison.Ordinal))
        {
            DataSourceDebugLog.Warn("Map", "Mapbox WebGL context lost; reloading map renderer");
            RecoverMapRenderer("WebGL context lost");
            return;
        }

        if (message?.StartsWith("map-error:", StringComparison.Ordinal) == true)
        {
            DataSourceDebugLog.Warn("Map", $"Mapbox renderer error | {message[10..]}");
            return;
        }

        if (!string.Equals(message, "map-state-applied", StringComparison.Ordinal))
        {
            return;
        }

        _mapStateInFlight = false;
        _mapStateSentAt = DateTimeOffset.MinValue;
        SendPendingMapState();
    }

    private void AttachWebViewHandlers()
    {
        var core = MapWebView.CoreWebView2;
        if (core is null)
        {
            return;
        }

        if (!_webMessageHandlerAttached)
        {
            core.WebMessageReceived += OnWebMessageReceived;
            _webMessageHandlerAttached = true;
        }

        if (!_processFailedHandlerAttached)
        {
            core.ProcessFailed += OnWebViewProcessFailed;
            _processFailedHandlerAttached = true;
        }
    }

    private void DetachWebViewHandlers()
    {
        var core = MapWebView.CoreWebView2;
        if (core is null)
        {
            return;
        }

        if (_webMessageHandlerAttached)
        {
            core.WebMessageReceived -= OnWebMessageReceived;
            _webMessageHandlerAttached = false;
        }

        if (_processFailedHandlerAttached)
        {
            core.ProcessFailed -= OnWebViewProcessFailed;
            _processFailedHandlerAttached = false;
        }
    }

    private void OnWebViewProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        DataSourceDebugLog.Warn("Map", $"WebView2 process failed | kind={e.ProcessFailedKind}");
        RecoverMapRenderer($"WebView2 process failed ({e.ProcessFailedKind})");
    }

    private void OnMapWatchdogTick(object? sender, EventArgs e)
    {
        if (_mapShouldRender && _webViewReady && _mapHeartbeatWatchStartedAt != DateTimeOffset.MinValue)
        {
            var lastHeartbeat = _lastMapHeartbeatAt == DateTimeOffset.MinValue
                ? _mapHeartbeatWatchStartedAt
                : _lastMapHeartbeatAt;
            if (DateTimeOffset.UtcNow - lastHeartbeat >= TimeSpan.FromSeconds(12))
            {
                DataSourceDebugLog.Warn("Map", "Mapbox WebView2 stopped sending renderer heartbeats");
                RecoverMapRenderer("renderer heartbeat timeout");
                return;
            }
        }

        if (!_mapStateInFlight || _mapStateSentAt == DateTimeOffset.MinValue ||
            DateTimeOffset.UtcNow - _mapStateSentAt < TimeSpan.FromSeconds(5))
        {
            return;
        }

        DataSourceDebugLog.Warn("Map", "Mapbox renderer stopped acknowledging map updates");
        RecoverMapRenderer("map update acknowledgement timeout");
    }

    private void RecoverMapRenderer(string reason)
    {
        var now = DateTimeOffset.UtcNow;
        if (!_webViewReady || MapWebView.CoreWebView2 is null ||
            now - _lastMapRecoveryAt < TimeSpan.FromSeconds(15))
        {
            return;
        }

        _lastMapRecoveryAt = now;
        _webViewReady = false;
        _mapStateInFlight = false;
        _mapStateSentAt = DateTimeOffset.MinValue;
        StatusText.Text = "Map restarting...";
        StatusText.Visibility = Visibility.Visible;
        DataSourceDebugLog.Warn("Map", $"Reloading WebView2 map | reason={reason}");
        MapWebView.CoreWebView2.Reload();
    }

    private static string CreateMapHtml() =>
        """
        <!doctype html>
        <html>
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <link href="https://api.mapbox.com/mapbox-gl-js/v3.9.4/mapbox-gl.css" rel="stylesheet">
          <style>
            html, body, #map {
              width: 100%;
              height: 100%;
              margin: 0;
              overflow: hidden;
              background: #071015;
            }

            .mapboxgl-control-container {
              font-family: Consolas, monospace;
              font-size: 10px;
            }

            #status {
              position: absolute;
              left: 8px;
              bottom: 8px;
              padding: 4px 6px;
              background: rgba(7, 16, 21, 0.78);
              color: #F7C873;
              font-family: Consolas, monospace;
              font-size: 11px;
              display: none;
            }
          </style>
        </head>
        <body>
          <div id="map"></div>
          <div id="status"></div>
          <script src="https://api.mapbox.com/mapbox-gl-js/v3.9.4/mapbox-gl.js"></script>
          <script>
            const metersPerNauticalMile = 1852.0;
            const webMercatorMetersPerPixelAtEquator = 78271.516964;
            const statusEl = document.getElementById('status');
            let map = null;
            let currentToken = '';
            let currentStyle = '';
            const mapUpdateIntervalMs = 500;
            let pendingState = null;
            let updateTimer = null;

            const setStatus = text => {
              statusEl.textContent = text || '';
              statusEl.style.display = text ? 'block' : 'none';
            };

            const calculateZoom = state => {
              const rect = map.getContainer().getBoundingClientRect();
              const radiusPixels = Math.max(12, Math.min(rect.width, rect.height) / 2 - 8);
              const rangeMeters = Math.max(1, state.selectedRangeNm) * metersPerNauticalMile;
              const metersPerPixel = rangeMeters / radiusPixels;
              const latitudeScale = Math.max(Math.cos(state.latitudeDeg * Math.PI / 180.0), 0.05);
              const zoom = Math.log2(webMercatorMetersPerPixelAtEquator * latitudeScale / metersPerPixel);
              return Math.max(0, Math.min(18, zoom));
            };

            const ensureMap = state => {
              const token = (state.mapboxAccessToken || '').trim();
              const style = (state.mapboxStyleUrl || '').trim() || 'mapbox://styles/mapbox/outdoors-v12';
              if (!token) {
                setStatus('Map unavailable');
                return false;
              }

              if (map && token === currentToken && style === currentStyle) {
                return true;
              }

              if (map) {
                map.remove();
                map = null;
              }

              currentToken = token;
              currentStyle = style;
              mapboxgl.accessToken = token;
              map = new mapboxgl.Map({
                container: 'map',
                style,
                center: [state.longitudeDeg, state.latitudeDeg],
                zoom: 7,
                bearing: 0,
                pitch: 0,
                fadeDuration: 0,
                renderWorldCopies: false,
                attributionControl: true,
                interactive: false,
              });

              map.dragPan.disable();
              map.scrollZoom.disable();
              map.boxZoom.disable();
              map.dragRotate.disable();
              map.keyboard.disable();
              map.doubleClickZoom.disable();
              map.touchZoomRotate.disable();
              map.on('error', event => {
                const message = event?.error?.message || 'Mapbox error';
                setStatus(`Map unavailable: ${message}`);
                window.chrome?.webview?.postMessage(`map-error:${message}`);
              });
              map.on('webglcontextlost', event => {
                event.originalEvent?.preventDefault?.();
                setStatus('Map restarting...');
                window.chrome?.webview?.postMessage('map-webgl-context-lost');
              });
              map.on('load', () => {
                setStatus('');
              });
              return true;
            };

            const acknowledgeMapState = () => {
              window.chrome?.webview?.postMessage('map-state-applied');
            };

            const applyLatestMapState = () => {
              updateTimer = null;
              const state = pendingState;
              pendingState = null;
              if (!state) {
                acknowledgeMapState();
                return;
              }

              try {
                if (!ensureMap(state)) {
                  return;
                }

                setStatus('');
                map.jumpTo({
                  center: [state.longitudeDeg, state.latitudeDeg],
                  zoom: calculateZoom(state),
                  bearing: state.bearingDeg,
                  pitch: 0,
                });
              } finally {
                acknowledgeMapState();
              }
            };

            window.updateTacticalMap = state => {
              // Coalesce high-frequency render updates. WebView2/Mapbox keeps native
              // render resources alive while jumpTo calls are queued, so an update
              // for every scope frame can grow the process for the whole session.
              pendingState = state;
              if (updateTimer === null) {
                updateTimer = setTimeout(applyLatestMapState, mapUpdateIntervalMs);
              }
            };

            if (window.chrome?.webview) {
              window.chrome.webview.addEventListener('message', event => {
                window.updateTacticalMap(event.data);
              });
              setInterval(() => window.chrome.webview.postMessage('map-heartbeat'), 3000);
            }
          </script>
        </body>
        </html>
        """;

    private static string ResolveWebViewUserDataFolder()
    {
        return AppDataPaths.WebViewUserDataDirectory;
    }

    private static void ShowWebViewRuntimeMissingDialog()
    {
        var result = MessageBox.Show(
            "Microsoft Edge WebView2 Runtime is required for the map layer, but it is not installed.\n\nOpen the Microsoft download page now?",
            "WebView2 Runtime Required",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = WebView2RuntimeDownloadUrl,
            UseShellExecute = true
        });
    }

    private sealed record MapState(
        double LatitudeDeg,
        double LongitudeDeg,
        double SelectedRangeNm,
        double BearingDeg,
        string MapboxAccessToken,
        string MapboxStyleUrl);
}
