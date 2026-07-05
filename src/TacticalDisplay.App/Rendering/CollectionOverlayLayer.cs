using System.Globalization;
using System.Windows;
using System.Windows.Media;
using TacticalDisplay.App.Cloud;
using TacticalDisplay.Core.Models;

namespace TacticalDisplay.App.Rendering;

public static class CollectionOverlayLayer
{
    public static void Draw(DrawingContext dc, IReadOnlyList<MapFeature>? features, TacticalPicture picture,
        TacticalDisplaySettings settings, Point center, double radius, double ownshipHeadingDeg, double pixelsPerDip)
    {
        if (features is null) return;
        var viewport = new Rect(0, 0, center.X * 2, center.Y * 2);
        foreach (var feature in features
                     .Where(x => x.VisibleOnRadar && x.GeometryType == MapFeatureGeometryType.Point)
                     .OrderBy(x => x.OrderIndex)
                     .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!TryReadPoint(feature, out var latitude, out var longitude)) continue;
            var projected = ScopeProjection.ProjectGeographicToScope(center.X, center.Y, radius,
                picture.Ownship.LatitudeDeg, picture.Ownship.LongitudeDeg, latitude, longitude, settings.SelectedRangeNm,
                ownshipHeadingDeg, settings.OrientationMode == ScopeOrientationMode.HeadingUp, clampToRange: false);
            var point = new Point(projected.x, projected.y);
            if (!viewport.Contains(point)) continue;
            var color = ParseColor(feature.Color); var pen = new Pen(new SolidColorBrush(color), 1.6);
            dc.DrawEllipse(null, pen, point, 6, 6);
            dc.DrawLine(pen, new Point(point.X - 9, point.Y), new Point(point.X + 9, point.Y));
            dc.DrawLine(pen, new Point(point.X, point.Y - 9), new Point(point.X, point.Y + 9));
            if (!string.IsNullOrWhiteSpace(feature.TacticalLabel))
            {
                DrawLabel(dc, feature.TacticalLabel, point, color, pixelsPerDip);
            }
        }
    }

    private static bool TryReadPoint(MapFeature feature, out double latitude, out double longitude)
    {
        latitude = longitude = 0;
        return feature.Geometry.TryGetProperty("lat", out var latNode) && feature.Geometry.TryGetProperty("lon", out var lonNode) &&
               latNode.TryGetDouble(out latitude) && lonNode.TryGetDouble(out longitude) && latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180;
    }
    private static Color ParseColor(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            try { return (Color)ColorConverter.ConvertFromString(value); }
            catch (Exception ex) when (ex is FormatException or NotSupportedException or InvalidCastException) { }
        return Color.FromRgb(154, 250, 215);
    }
    private static void DrawLabel(DrawingContext dc, string text, Point point, Color color, double pixelsPerDip)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            10, new SolidColorBrush(color), pixelsPerDip);
        var origin = new Point(point.X - formatted.Width / 2, point.Y + 11);
        var halo = formatted.BuildGeometry(origin);
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(220, 0, 7, 10)), 3), halo);
        dc.DrawText(formatted, origin);
    }
}
