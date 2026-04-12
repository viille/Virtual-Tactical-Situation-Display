using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using TacticalDisplay.Core.Models;

namespace TacticalDisplay.App.Controls;

public partial class OpenFreeMapControl : UserControl
{
    private const double MetersPerNauticalMile = 1852.0;

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
            await MapWebView.EnsureCoreWebView2Async();
            MapWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            MapWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            MapWebView.NavigateToString(CreateMapHtml());
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Map unavailable: {ex.Message}";
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
            headingUp ? ownship.HeadingDeg : 0.0);
        var json = JsonSerializer.Serialize(state, JsonOptions);
        var script = $"window.updateOpenFreeMap && window.updateOpenFreeMap({json});";

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
          <link href="https://unpkg.com/maplibre-gl/dist/maplibre-gl.css" rel="stylesheet">
          <style>
            html, body, #map {
              width: 100%;
              height: 100%;
              margin: 0;
              overflow: hidden;
              background: #071015;
            }

            .maplibregl-control-container {
              font-family: Consolas, monospace;
              font-size: 10px;
            }
          </style>
        </head>
        <body>
          <div id="map"></div>
          <script src="https://unpkg.com/maplibre-gl/dist/maplibre-gl.js"></script>
          <script>
            const metersPerNauticalMile = 1852.0;
            const webMercatorMetersPerPixelAtEquator = 156543.03392;

            const calculateZoom = state => {
              const rect = map.getContainer().getBoundingClientRect();
              const radiusPixels = Math.max(1, Math.min(rect.width, rect.height) * 0.45);
              const rangeMeters = Math.max(1, state.selectedRangeNm) * metersPerNauticalMile;
              const metersPerPixel = rangeMeters / radiusPixels;
              const latitudeScale = Math.max(Math.cos(state.latitudeDeg * Math.PI / 180.0), 0.05);
              const zoom = Math.log2(webMercatorMetersPerPixelAtEquator * latitudeScale / metersPerPixel);
              return Math.max(0, Math.min(18, zoom));
            };

            const map = new maplibregl.Map({
              container: 'map',
              style: 'https://tiles.openfreemap.org/styles/liberty',
              center: [24.9633, 60.3172],
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

            window.updateOpenFreeMap = state => {
              map.jumpTo({
                center: [state.longitudeDeg, state.latitudeDeg],
                zoom: calculateZoom(state),
                bearing: state.bearingDeg,
                pitch: 0,
              });
            };
          </script>
        </body>
        </html>
        """;

    private sealed record MapState(
        double LatitudeDeg,
        double LongitudeDeg,
        double SelectedRangeNm,
        double BearingDeg);
}
