using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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
    private string? _pendingScript;

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
        _pendingScript = null;
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _webViewReady = e.IsSuccess;
        StatusText.Visibility = e.IsSuccess ? Visibility.Collapsed : Visibility.Visible;
        if (!e.IsSuccess)
        {
            StatusText.Text = $"Map unavailable: {e.WebErrorStatus}";
            return;
        }

        UpdateMapState();
        if (_pendingScript is not null)
        {
            var script = _pendingScript;
            _pendingScript = null;
            await ExecuteMapScriptAsync(script);
        }
    }

    private void UpdateMapState()
    {
        var settings = Settings;
        var picture = Picture;
        var showMap = settings?.ShowMapLayer == true && picture is not null;
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
            MapboxDefaults.ResolveStyleUrl(),
            MapboxDefaults.ResolveAreasStyleUrl(),
            settings.ShowControlledAirspaceLayer);
        var json = JsonSerializer.Serialize(state, JsonOptions);
        var script = $"window.updateTacticalMap && window.updateTacticalMap({json});";

        if (!_webViewReady)
        {
            _pendingScript = script;
            return;
        }

        _ = ExecuteMapScriptAsync(script);
    }

    private async Task ExecuteMapScriptAsync(string script)
    {
        if (MapWebView.CoreWebView2 is null)
        {
            _pendingScript = script;
            return;
        }

        try
        {
            await MapWebView.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch (InvalidOperationException)
        {
            _pendingScript = script;
        }
        catch (COMException)
        {
            _pendingScript = script;
        }
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
            let lastState = null;
            let currentAreasStyle = '';
            let areasOverlayPromise = null;
            let areasLayerIds = [];
            let areasSourceIds = [];

            const setStatus = text => {
              statusEl.textContent = text || '';
              statusEl.style.display = text ? 'block' : 'none';
            };

            const calculateZoom = state => {
              const rect = map.getContainer().getBoundingClientRect();
              const radiusPixels = Math.max(1, Math.min(rect.width, rect.height) * 0.45);
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
                currentAreasStyle = '';
                areasLayerIds = [];
                areasSourceIds = [];
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
              map.on('error', event => setStatus(`Map unavailable: ${event?.error?.message || 'Mapbox error'}`));
              map.on('load', () => {
                setStatus('');
                updateAreasOverlay(lastState);
              });
              return true;
            };

            const areasStyleApiUrl = (styleUrl, token) => {
              const trimmed = (styleUrl || '').trim();
              if (/^https?:\/\//i.test(trimmed)) {
                const separator = trimmed.includes('?') ? '&' : '?';
                return trimmed.includes('access_token=') ? trimmed : `${trimmed}${separator}access_token=${encodeURIComponent(token)}`;
              }
              const match = /^mapbox:\/\/styles\/([^/]+)\/([^/?#]+)/i.exec(trimmed);
              if (!match) return '';
              return `https://api.mapbox.com/styles/v1/${encodeURIComponent(match[1])}/${encodeURIComponent(match[2])}?access_token=${encodeURIComponent(token)}`;
            };

            const removeAreasOverlay = () => {
              if (!map) return;
              for (const id of [...areasLayerIds].reverse()) {
                if (map.getLayer(id)) {
                  map.removeLayer(id);
                }
              }
              for (const id of [...areasSourceIds].reverse()) {
                if (map.getSource(id)) {
                  map.removeSource(id);
                }
              }
              areasLayerIds = [];
              areasSourceIds = [];
              currentAreasStyle = '';
            };

            const setAreasVisibility = visible => {
              if (!map) return;
              const visibility = visible ? 'visible' : 'none';
              for (const id of areasLayerIds) {
                if (map.getLayer(id)) {
                  map.setLayoutProperty(id, 'visibility', visibility);
                }
              }
            };

            const addAreasOverlay = async state => {
              if (!map || !map.isStyleLoaded()) return;
              const styleUrl = (state?.mapboxAreasStyleUrl || '').trim();
              const token = (state?.mapboxAccessToken || '').trim();
              if (!styleUrl || !token) {
                removeAreasOverlay();
                return;
              }

              if (currentAreasStyle === styleUrl && areasLayerIds.length > 0) {
                setAreasVisibility(!!state.showControlledAirspaceLayer);
                return;
              }

              const apiUrl = areasStyleApiUrl(styleUrl, token);
              if (!apiUrl) {
                removeAreasOverlay();
                return;
              }

              removeAreasOverlay();
              const response = await fetch(apiUrl, { cache: 'force-cache' });
              if (!response.ok) throw new Error(`Areas style ${response.status}`);
              const style = await response.json();
              const sourceMap = new globalThis.Map();
              for (const [sourceId, source] of Object.entries(style.sources || {})) {
                const prefixedSourceId = `areas-${sourceId}`;
                if (!map.getSource(prefixedSourceId)) {
                  map.addSource(prefixedSourceId, JSON.parse(JSON.stringify(source)));
                  areasSourceIds.push(prefixedSourceId);
                }
                sourceMap.set(sourceId, prefixedSourceId);
              }

              for (const layer of style.layers || []) {
                if (!layer || layer.type === 'background') continue;
                const copy = JSON.parse(JSON.stringify(layer));
                copy.id = `areas-${copy.id}`;
                if (copy.source && sourceMap.has(copy.source)) {
                  copy.source = sourceMap.get(copy.source);
                } else if (copy.source) {
                  continue;
                }
                copy.layout = copy.layout || {};
                copy.layout.visibility = state.showControlledAirspaceLayer ? 'visible' : 'none';
                if (!map.getLayer(copy.id)) {
                  map.addLayer(copy);
                  areasLayerIds.push(copy.id);
                }
              }

              currentAreasStyle = styleUrl;
            };

            const updateAreasOverlay = state => {
              if (!state || !map || !map.isStyleLoaded()) return;
              if (areasOverlayPromise) return;
              areasOverlayPromise = addAreasOverlay(state)
                .catch(() => setStatus('Areas unavailable'))
                .finally(() => { areasOverlayPromise = null; });
            };

            window.updateTacticalMap = state => {
              lastState = state;
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
              updateAreasOverlay(state);
            };
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
        string MapboxStyleUrl,
        string MapboxAreasStyleUrl,
        bool ShowControlledAirspaceLayer);
}
