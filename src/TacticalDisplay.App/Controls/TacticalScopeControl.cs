using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TacticalDisplay.Core.Formatting;
using TacticalDisplay.Core.Math;
using TacticalDisplay.Core.Models;
using TacticalDisplay.App.Rendering;

namespace TacticalDisplay.App.Controls;

public sealed class TacticalScopeControl : FrameworkElement
{
    private const double LabelPaddingX = 4;
    private const double LabelPaddingY = 2;
    private const double LabelMargin = 8;
    private const double LabelLineGap = 1;
    private const double LabelSeparation = 4;
    private const double MaxOverlayCoordinate = 100_000;

    private readonly List<(string id, Point point)> _hitTargets = [];
    private readonly List<HitLabel> _hitLabels = [];
    private DragState? _dragState;

    public static readonly DependencyProperty PictureProperty = DependencyProperty.Register(
        nameof(Picture),
        typeof(TacticalPicture),
        typeof(TacticalScopeControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SettingsProperty = DependencyProperty.Register(
        nameof(Settings),
        typeof(TacticalDisplaySettings),
        typeof(TacticalScopeControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ManualTargetMetadataProperty = DependencyProperty.Register(
        nameof(ManualTargetMetadata),
        typeof(IReadOnlyDictionary<string, ManualTargetMetadata>),
        typeof(TacticalScopeControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AirspacesProperty = DependencyProperty.Register(
        nameof(Airspaces),
        typeof(IReadOnlyList<AirspaceArea>),
        typeof(TacticalScopeControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty InterceptTargetIdProperty = DependencyProperty.Register(
        nameof(InterceptTargetId),
        typeof(string),
        typeof(TacticalScopeControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty InterceptSelectionArmedProperty = DependencyProperty.Register(
        nameof(InterceptSelectionArmed),
        typeof(bool),
        typeof(TacticalScopeControl),
        new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty TargetLabelBackgroundOpacityProperty = DependencyProperty.Register(
        nameof(TargetLabelBackgroundOpacity),
        typeof(double),
        typeof(TacticalScopeControl),
        new FrameworkPropertyMetadata(0.75, FrameworkPropertyMetadataOptions.AffectsRender));

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

    public IReadOnlyDictionary<string, ManualTargetMetadata>? ManualTargetMetadata
    {
        get => (IReadOnlyDictionary<string, ManualTargetMetadata>?)GetValue(ManualTargetMetadataProperty);
        set => SetValue(ManualTargetMetadataProperty, value);
    }

    public IReadOnlyList<AirspaceArea>? Airspaces
    {
        get => (IReadOnlyList<AirspaceArea>?)GetValue(AirspacesProperty);
        set => SetValue(AirspacesProperty, value);
    }

    public string? InterceptTargetId
    {
        get => (string?)GetValue(InterceptTargetIdProperty);
        set => SetValue(InterceptTargetIdProperty, value);
    }

    public bool InterceptSelectionArmed
    {
        get => (bool)GetValue(InterceptSelectionArmedProperty);
        set => SetValue(InterceptSelectionArmedProperty, value);
    }

    public double TargetLabelBackgroundOpacity
    {
        get => (double)GetValue(TargetLabelBackgroundOpacityProperty);
        set => SetValue(TargetLabelBackgroundOpacityProperty, value);
    }

    public event EventHandler<ScopeTargetClickEventArgs>? TargetClicked;
    public event EventHandler<ScopeLabelMovedEventArgs>? LabelMoved;

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        _hitTargets.Clear();
        _hitLabels.Clear();
        DrawBackground(dc);
        if (Picture is null || Settings is null)
        {
            return;
        }

        var center = new Point(RenderSize.Width / 2.0, RenderSize.Height / 2.0);
        var radius = System.Math.Min(RenderSize.Width, RenderSize.Height) * 0.45;
        var ownshipHeadingDeg = Picture.Ownship.HeadingDeg;
        DrawRings(dc, center, radius);
        DrawFrameCompass(dc, center, ownshipHeadingDeg);
        DrawHeadingReadout(dc, center, ownshipHeadingDeg);
        DrawOptionalOverlay(() => DrawAirspaces(dc, center, radius, ownshipHeadingDeg));
        DrawOptionalOverlay(() => DrawBullseye(dc, center, radius, ownshipHeadingDeg));
        DrawOptionalOverlay(() => DrawIntercept(dc, center, radius, ownshipHeadingDeg));
        DrawOwnship(dc, center, ownshipHeadingDeg);
        DrawTargets(dc, center, radius, ownshipHeadingDeg);
    }

    private static void DrawOptionalOverlay(Action draw)
    {
        try
        {
            draw();
        }
        catch
        {
            // Optional overlays must never prevent traffic from rendering.
        }
    }

    private void DrawBackground(DrawingContext dc)
    {
        if (RenderSize.Width <= 0 || RenderSize.Height <= 0)
        {
            return;
        }

        if (Settings?.ShowMapLayer == true)
        {
            return;
        }

        var rect = new Rect(0, 0, RenderSize.Width, RenderSize.Height);
        var brush = new LinearGradientBrush(
            Color.FromRgb(4, 12, 18),
            Color.FromRgb(8, 26, 28),
            new Point(0, 0),
            new Point(1, 1));
        dc.DrawRectangle(brush, null, rect);
    }

    private void DrawRings(DrawingContext dc, Point center, double radius)
    {
        if (Settings is null || !Settings.ShowRangeRings)
        {
            return;
        }

        var ringHaloPen = new Pen(new SolidColorBrush(Color.FromArgb(230, 0, 8, 10)), 3.6);
        var ringPen = new Pen(new SolidColorBrush(Color.FromArgb(210, 180, 255, 235)), 1.2);
        for (var i = 1; i <= 4; i++)
        {
            var ringRadius = radius * i / 4.0;
            dc.DrawEllipse(null, ringHaloPen, center, ringRadius, ringRadius);
            dc.DrawEllipse(null, ringPen, center, ringRadius, ringRadius);
            var label = $"{Settings.SelectedRangeNm * i / 4:0} NM";
            DrawCenteredHaloText(dc, label, center.X, center.Y - ringRadius - 13, Color.FromRgb(175, 255, 220), 10, FontWeights.SemiBold);
        }
    }

    private void DrawFrameCompass(DrawingContext dc, Point center, double ownshipHeadingDeg)
    {
        if (Settings is null || RenderSize.Width <= 24 || RenderSize.Height <= 24)
        {
            return;
        }

        var headingOffset = Settings.OrientationMode == ScopeOrientationMode.HeadingUp ? ownshipHeadingDeg : 0;
        var compassRadius = Math.Max(12, Math.Min(RenderSize.Width, RenderSize.Height) / 2.0 - 8);
        var haloPen = new Pen(new SolidColorBrush(Color.FromArgb(220, 0, 7, 10)), 3.4);
        var minorPen = new Pen(new SolidColorBrush(Color.FromArgb(155, 170, 205, 210)), 1.1);
        var majorPen = new Pen(new SolidColorBrush(Color.FromArgb(210, 215, 245, 240)), 1.7);
        var ringHaloPen = new Pen(new SolidColorBrush(Color.FromArgb(180, 0, 7, 10)), 3.2);
        var ringPen = new Pen(new SolidColorBrush(Color.FromArgb(140, 170, 205, 210)), 1.0);

        dc.DrawEllipse(null, ringHaloPen, center, compassRadius, compassRadius);
        dc.DrawEllipse(null, ringPen, center, compassRadius, compassRadius);

        for (var bearing = 0; bearing < 360; bearing += 10)
        {
            var displayBearing = GeoMath.NormalizeDegrees(bearing - headingOffset);
            var edge = PointOnCircle(center, compassRadius, displayBearing);
            var length = bearing % 30 == 0 ? 12.0 : 7.0;
            var inner = PointOnCircle(center, compassRadius - length, displayBearing);
            var pen = bearing % 30 == 0 ? majorPen : minorPen;
            dc.DrawLine(haloPen, edge, inner);
            dc.DrawLine(pen, edge, inner);
        }

        DrawCompassCardinal(dc, "N", 0, headingOffset, center, compassRadius);
        DrawCompassCardinal(dc, "E", 90, headingOffset, center, compassRadius);
        DrawCompassCardinal(dc, "S", 180, headingOffset, center, compassRadius);
        DrawCompassCardinal(dc, "W", 270, headingOffset, center, compassRadius);
    }

    private void DrawCompassCardinal(
        DrawingContext dc,
        string label,
        double bearing,
        double headingOffset,
        Point center,
        double compassRadius)
    {
        var displayBearing = GeoMath.NormalizeDegrees(bearing - headingOffset);
        var point = PointOnCircle(center, compassRadius - 27, displayBearing);
        var formatted = CreateFormattedText(label, Color.FromRgb(236, 250, 246), 18, FontWeights.Bold);
        var origin = new Point(point.X - formatted.Width / 2.0, point.Y - formatted.Height / 2.0);
        var geometry = formatted.BuildGeometry(origin);
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(245, 0, 7, 10)), 3.2), geometry);
        dc.DrawText(formatted, origin);
    }

    private static Point PointOnCircle(Point center, double radius, double bearingDeg)
    {
        var rad = bearingDeg * Math.PI / 180.0;
        return new Point(
            center.X + radius * Math.Sin(rad),
            center.Y - radius * Math.Cos(rad));
    }

    private void DrawHeadingReadout(DrawingContext dc, Point center, double ownHeadingDeg)
    {
        if (RenderSize.Width <= 0)
        {
            return;
        }

        DrawTopReadout(dc, $"HDG {GeoMath.NormalizeDegrees(ownHeadingDeg):000}", center.X, 10.0, Color.FromRgb(175, 255, 225));
    }

    private void DrawTopReadout(DrawingContext dc, string text, double centerX, double y, Color foreground)
    {
        var formatted = CreateFormattedText(text, foreground, 14, FontWeights.Bold);
        var point = new Point(centerX - formatted.Width / 2.0, y);
        if (RenderSize.Width > 0)
        {
            point.X = Math.Clamp(point.X, 2, Math.Max(2, RenderSize.Width - formatted.Width - 18));
        }

        var background = new SolidColorBrush(Color.FromArgb(176, 3, 10, 16));
        var border = new Pen(new SolidColorBrush(Color.FromArgb(110, 110, 180, 170)), 1);
        dc.DrawRoundedRectangle(
            background,
            border,
            new Rect(point.X - 8, point.Y - 3, formatted.Width + 16, formatted.Height + 6),
            2,
            2);
        DrawTextHalo(dc, CreateFormattedText(text, Color.FromArgb(245, 0, 8, 10), 14, FontWeights.Bold), point);
        dc.DrawText(formatted, point);
    }

    private void DrawOwnship(DrawingContext dc, Point center, double headingDeg)
    {
        var rotation = Settings?.OrientationMode == ScopeOrientationMode.HeadingUp ? 0 : headingDeg;
        var haloPen = new Pen(new SolidColorBrush(Color.FromArgb(245, 0, 8, 10)), 3.2);
        var symbolPen = new Pen(new SolidColorBrush(Color.FromRgb(220, 252, 246)), 1.5);
        dc.PushTransform(new RotateTransform(rotation, center.X, center.Y));
        DrawOwnshipCross(dc, center, haloPen);
        DrawOwnshipCross(dc, center, symbolPen);
        dc.Pop();
    }

    private static void DrawOwnshipCross(DrawingContext dc, Point center, Pen pen)
    {
        dc.DrawLine(pen, new Point(center.X, center.Y - 13), new Point(center.X, center.Y + 13));
        dc.DrawLine(pen, new Point(center.X - 13, center.Y), new Point(center.X + 13, center.Y));
        dc.DrawLine(pen, new Point(center.X - 4.5, center.Y + 13), new Point(center.X + 4.5, center.Y + 13));
    }

    private void DrawAirspaces(DrawingContext dc, Point center, double radius, double ownshipHeadingDeg)
    {
        if (Picture is null ||
            Settings is null ||
            Airspaces is null ||
            !Settings.ShowAirspaceBoundaries)
        {
            return;
        }

        var opacity = Math.Clamp(Settings.AirspaceOpacity, 0.1, 1.0);
        var pen = new Pen(new SolidColorBrush(WithScaledAlpha(210, 247, 200, 115, opacity)), 1.2);
        var activeFill = new SolidColorBrush(WithScaledAlpha(34, 247, 200, 115, opacity));
        var adizPen = new Pen(new SolidColorBrush(WithScaledAlpha(220, 255, 80, 80, opacity)), 1.4);
        var adizFill = new SolidColorBrush(WithScaledAlpha(28, 255, 80, 80, opacity));
        var inactivePen = new Pen(new SolidColorBrush(WithScaledAlpha(70, 110, 180, 170, opacity)), 0.8);
        var inactiveFill = new SolidColorBrush(WithScaledAlpha(12, 110, 180, 170, opacity));

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, RenderSize.Width, RenderSize.Height)));
        try
        {
            foreach (var airspace in Airspaces)
            {
                var isAdiz = IsAdizAirspace(airspace);
                if (Settings.ShowOnlyActiveAirspaceBoundaries && !airspace.IsActive && !isAdiz)
                {
                    continue;
                }

                foreach (var polygon in airspace.Polygons)
                {
                    var projected = ProjectAirspacePolygon(polygon, center, radius, ownshipHeadingDeg);
                    if (projected.Count < 2 || !IntersectsViewport(projected))
                    {
                        continue;
                    }

                    var geometry = BuildAirspaceGeometry(projected);
                    if (isAdiz)
                    {
                        dc.DrawGeometry(adizFill, adizPen, geometry);
                    }
                    else
                    {
                        dc.DrawGeometry(airspace.IsActive ? activeFill : inactiveFill, airspace.IsActive ? pen : inactivePen, geometry);
                    }
                }

                if ((airspace.IsActive || isAdiz) && !string.IsNullOrWhiteSpace(airspace.Name))
                {
                    DrawAirspaceLabel(dc, airspace, center, radius, opacity, ownshipHeadingDeg, isAdiz);
                }
            }
        }
        finally
        {
            dc.Pop();
        }
    }

    private IReadOnlyList<Point> ProjectAirspacePolygon(AirspacePolygon polygon, Point center, double radius, double ownshipHeadingDeg)
    {
        if (Picture is null || Settings is null)
        {
            return [];
        }

        var projected = new List<Point>(polygon.Exterior.Count);
        foreach (var coordinate in polygon.Exterior)
        {
            var point = ScopeProjection.ProjectGeographicToScope(
                center.X,
                center.Y,
                radius,
                Picture.Ownship.LatitudeDeg,
                Picture.Ownship.LongitudeDeg,
                coordinate.LatitudeDeg,
                coordinate.LongitudeDeg,
                Settings.SelectedRangeNm,
                ownshipHeadingDeg,
                Settings.OrientationMode == ScopeOrientationMode.HeadingUp,
                clampToRange: false);
            if (!IsDrawableOverlayPoint(point.x, point.y))
            {
                continue;
            }

            projected.Add(new Point(point.x, point.y));
        }

        return projected;
    }

    private static StreamGeometry BuildAirspaceGeometry(IReadOnlyList<Point> points)
    {
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        ctx.BeginFigure(points[0], true, true);
        for (var i = 1; i < points.Count; i++)
        {
            ctx.LineTo(points[i], true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    private bool IntersectsViewport(IReadOnlyList<Point> points)
    {
        var viewport = new Rect(0, 0, RenderSize.Width, RenderSize.Height);
        var bounds = new Rect(points[0], points[0]);
        foreach (var point in points)
        {
            bounds.Union(point);
            if (viewport.Contains(point))
            {
                return true;
            }
        }

        return bounds.IntersectsWith(viewport) || bounds.Contains(viewport);
    }

    private static bool IsDrawableOverlayPoint(double x, double y) =>
        double.IsFinite(x) &&
        double.IsFinite(y) &&
        Math.Abs(x) <= MaxOverlayCoordinate &&
        Math.Abs(y) <= MaxOverlayCoordinate;

    private static bool IsAdizAirspace(AirspaceArea airspace) =>
        airspace.Name.Contains("ADIZ", StringComparison.OrdinalIgnoreCase) ||
        airspace.Name.Equals("EFR100", StringComparison.OrdinalIgnoreCase) ||
        airspace.Name.Equals("EFD400", StringComparison.OrdinalIgnoreCase) ||
        airspace.Type.Contains("ADIZ", StringComparison.OrdinalIgnoreCase);

    private void DrawAirspaceLabel(
        DrawingContext dc,
        AirspaceArea airspace,
        Point center,
        double radius,
        double opacity,
        double ownshipHeadingDeg,
        bool isAdiz)
    {
        if (Picture is null || Settings is null)
        {
            return;
        }

        var coordinates = airspace.Polygons.SelectMany(p => p.Exterior).ToList();
        if (coordinates.Count == 0)
        {
            return;
        }

        var latitude = coordinates.Average(c => c.LatitudeDeg);
        var longitude = coordinates.Average(c => c.LongitudeDeg);
        var range = GeoMath.DistanceNm(Picture.Ownship.LatitudeDeg, Picture.Ownship.LongitudeDeg, latitude, longitude);
        if (range > Settings.SelectedRangeNm)
        {
            return;
        }

        var point = ScopeProjection.ProjectGeographicToScope(
            center.X,
            center.Y,
            radius,
            Picture.Ownship.LatitudeDeg,
            Picture.Ownship.LongitudeDeg,
            latitude,
            longitude,
            Settings.SelectedRangeNm,
            ownshipHeadingDeg,
            Settings.OrientationMode == ScopeOrientationMode.HeadingUp);
        var labelColor = isAdiz
            ? WithScaledAlpha(255, 255, 120, 120, opacity)
            : WithScaledAlpha(255, 247, 200, 115, opacity);
        DrawCenteredText(dc, BuildAirspaceLabelText(airspace), point.x, point.y, labelColor, 11, FontWeights.SemiBold);
    }

    private static string BuildAirspaceLabelText(AirspaceArea airspace)
    {
        if (airspace.IsActive &&
            airspace.ActiveLowerAltitudeFt.HasValue &&
            airspace.ActiveUpperAltitudeFt.HasValue)
        {
            return $"{airspace.Name} {airspace.ActiveLowerAltitudeFt.Value}-{airspace.ActiveUpperAltitudeFt.Value}";
        }

        var lower = airspace.LowerFlightLevel.HasValue
            ? airspace.LowerFlightLevel.Value == 0 ? "SFC" : $"FL{airspace.LowerFlightLevel.Value:000}"
            : string.Empty;
        var upper = airspace.UpperFlightLevel.HasValue ? $"FL{airspace.UpperFlightLevel.Value:000}" : string.Empty;
        return string.IsNullOrWhiteSpace(lower) || string.IsNullOrWhiteSpace(upper)
            ? airspace.Name
            : $"{airspace.Name} {lower}-{upper}";
    }

    private static Color WithScaledAlpha(byte alpha, byte red, byte green, byte blue, double opacity) =>
        Color.FromArgb((byte)Math.Clamp(alpha * opacity, 0, 255), red, green, blue);

    private void DrawBullseye(DrawingContext dc, Point center, double radius, double ownshipHeadingDeg)
    {
        if (Picture is null ||
            Settings is null ||
            !Settings.ShowBullseye ||
            !Settings.BullseyeLatitudeDeg.HasValue ||
            !Settings.BullseyeLongitudeDeg.HasValue)
        {
            return;
        }

        var range = GeoMath.DistanceNm(
            Picture.Ownship.LatitudeDeg,
            Picture.Ownship.LongitudeDeg,
            Settings.BullseyeLatitudeDeg.Value,
            Settings.BullseyeLongitudeDeg.Value);
        if (range > Settings.SelectedRangeNm)
        {
            return;
        }

        var bearing = GeoMath.InitialBearingDeg(
            Picture.Ownship.LatitudeDeg,
            Picture.Ownship.LongitudeDeg,
            Settings.BullseyeLatitudeDeg.Value,
            Settings.BullseyeLongitudeDeg.Value);
        var point = ScopeProjection.ProjectGeographicToScope(
            center.X,
            center.Y,
            radius,
            Picture.Ownship.LatitudeDeg,
            Picture.Ownship.LongitudeDeg,
            Settings.BullseyeLatitudeDeg.Value,
            Settings.BullseyeLongitudeDeg.Value,
            Settings.SelectedRangeNm,
            ownshipHeadingDeg,
            Settings.OrientationMode == ScopeOrientationMode.HeadingUp);
        var p = new Point(point.x, point.y);
        if (!new Rect(0, 0, RenderSize.Width, RenderSize.Height).Contains(p))
        {
            return;
        }

        var brush = new SolidColorBrush(Color.FromRgb(247, 200, 115));
        var pen = new Pen(brush, 1.4);
        dc.DrawEllipse(null, pen, p, 8, 8);
        dc.DrawLine(pen, new Point(p.X - 12, p.Y), new Point(p.X + 12, p.Y));
        dc.DrawLine(pen, new Point(p.X, p.Y - 12), new Point(p.X, p.Y + 12));
        DrawText(dc, $"BULL {bearing:000}/{range:0.0}", p.X + 12, p.Y + 8, Color.FromRgb(247, 200, 115), 11, FontWeights.SemiBold);
    }

    private void DrawTargets(DrawingContext dc, Point center, double radius, double ownshipHeadingDeg)
    {
        if (Picture is null || Settings is null)
        {
            return;
        }

        var visibleTargets = Picture.Targets
            .Where(t => t.RangeNm <= Settings.SelectedRangeNm);

        if (Settings.Declutter)
        {
            // Declutter mode keeps the closest non-stale contacts and removes extra noise.
            visibleTargets = visibleTargets
                .Where(t => !t.IsStale)
                .OrderBy(t => t.RangeNm)
                .Take(12);
        }

        var targetsToDraw = visibleTargets.ToList();
        var labelRects = new List<Rect>(targetsToDraw.Count);

        foreach (var target in targetsToDraw)
        {
            var projected = ScopeProjection.ProjectToScope(
                center.X,
                center.Y,
                radius,
                target.RangeNm,
                target.BearingDegTrue,
                Settings.SelectedRangeNm,
                ownshipHeadingDeg,
                Settings.OrientationMode == ScopeOrientationMode.HeadingUp);

            if (Settings.TrailsEnabled && !Settings.Declutter)
            {
                DrawTrail(dc, target, center, radius, ownshipHeadingDeg);
            }

            var projectedPoint = new Point(projected.x, projected.y);
            DrawTargetSymbol(
                dc,
                projectedPoint,
                target,
                ownshipHeadingDeg,
                Settings.OrientationMode == ScopeOrientationMode.HeadingUp,
                Settings.TargetSymbolScale);
            if (IsInterceptTarget(target.Id))
            {
                DrawInterceptTargetHighlight(dc, projectedPoint, Settings.TargetSymbolScale);
            }

            _hitTargets.Add((target.Id, projectedPoint));
            var effectiveLabelMode = Settings.LabelMode;
            if (effectiveLabelMode != LabelMode.Off && !IsLabelHidden(target.Id))
            {
                DrawTargetLabel(dc, target, projectedPoint, effectiveLabelMode, labelRects);
            }
        }
    }

    private void DrawTrail(DrawingContext dc, ComputedTarget target, Point center, double radius, double ownshipHeadingDeg)
    {
        if (Picture is null || Settings is null || target.History.Count < 2)
        {
            return;
        }

        var pen = new Pen(new SolidColorBrush(Color.FromArgb(90, 140, 230, 220)), 1);
        Point? previous = null;
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, RenderSize.Width, RenderSize.Height)));
        foreach (var point in target.History)
        {
            var range = GeoMath.DistanceNm(Picture.Ownship.LatitudeDeg, Picture.Ownship.LongitudeDeg, point.LatitudeDeg, point.LongitudeDeg);
            var projection = ScopeProjection.ProjectGeographicToScope(
                center.X,
                center.Y,
                radius,
                Picture.Ownship.LatitudeDeg,
                Picture.Ownship.LongitudeDeg,
                point.LatitudeDeg,
                point.LongitudeDeg,
                Settings.SelectedRangeNm,
                ownshipHeadingDeg,
                Settings.OrientationMode == ScopeOrientationMode.HeadingUp,
                clampToRange: false);
            var current = new Point(projection.x, projection.y);
            if (previous is not null)
            {
                dc.DrawLine(pen, previous.Value, current);
            }

            previous = current;
        }

        dc.Pop();
    }

    private static void DrawTargetSymbol(
        DrawingContext dc,
        Point p,
        ComputedTarget target,
        double ownHeadingDeg,
        bool headingUp,
        double symbolScale)
    {
        var brush = GetBrush(target);
        var dim = target.IsStale ? 0.4 : 1.0;
        brush = brush.Clone();
        brush.Opacity = dim;
        var scale = Math.Clamp(symbolScale, 0.6, 1.8);
        var pen = new Pen(brush, 1.5 * scale);
        DrawTargetHeading(dc, p, target, pen, ownHeadingDeg, headingUp, scale);

        switch (target.Category)
        {
            case TargetCategory.Friend:
                dc.DrawEllipse(null, pen, p, 5 * scale, 5 * scale);
                break;
            case TargetCategory.Enemy:
                dc.DrawLine(pen, new Point(p.X - 5 * scale, p.Y - 5 * scale), new Point(p.X + 5 * scale, p.Y + 5 * scale));
                dc.DrawLine(pen, new Point(p.X - 5 * scale, p.Y + 5 * scale), new Point(p.X + 5 * scale, p.Y - 5 * scale));
                break;
            case TargetCategory.Package:
                DrawDiamond(dc, p, pen, scale);
                break;
            case TargetCategory.Support:
                dc.DrawRectangle(null, pen, new Rect(p.X - 4 * scale, p.Y - 4 * scale, 8 * scale, 8 * scale));
                break;
            default:
                dc.DrawEllipse(brush, null, p, 2.5 * scale, 2.5 * scale);
                break;
        }
    }

    private bool IsInterceptTarget(string targetId) =>
        !string.IsNullOrWhiteSpace(InterceptTargetId) &&
        string.Equals(InterceptTargetId, targetId, StringComparison.OrdinalIgnoreCase);

    private void DrawIntercept(DrawingContext dc, Point center, double radius, double ownshipHeadingDeg)
    {
        if (Picture is null || Settings is null || string.IsNullOrWhiteSpace(InterceptTargetId))
        {
            return;
        }

        var target = Picture.Targets.FirstOrDefault(t => IsInterceptTarget(t.Id));
        if (target is null || target.RangeNm > Settings.SelectedRangeNm)
        {
            return;
        }

        var targetPoint = ProjectTargetPoint(target, center, radius, ownshipHeadingDeg);
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(255, 210, 90)), 1.4)
        {
            DashStyle = new DashStyle([6, 4], 0)
        };
        dc.DrawLine(pen, center, targetPoint);

        var solution = CalculateInterceptSolution(Picture.Ownship, target);
        if (solution.HasSolution)
        {
            var interceptPoint = ProjectGeographicPoint(
                solution.LatitudeDeg,
                solution.LongitudeDeg,
                center,
                radius,
                ownshipHeadingDeg,
                clampToRange: false);
            if (IsDrawableOverlayPoint(interceptPoint.X, interceptPoint.Y) &&
                IntersectsViewport([center, interceptPoint]))
            {
                var pointPen = new Pen(new SolidColorBrush(Color.FromRgb(255, 210, 90)), 1.5);
                dc.DrawEllipse(null, pointPen, interceptPoint, 7, 7);
                dc.DrawLine(pointPen, new Point(interceptPoint.X - 10, interceptPoint.Y), new Point(interceptPoint.X + 10, interceptPoint.Y));
                dc.DrawLine(pointPen, new Point(interceptPoint.X, interceptPoint.Y - 10), new Point(interceptPoint.X, interceptPoint.Y + 10));
                dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(110, 255, 210, 90)), 1), targetPoint, interceptPoint);
            }
        }

        DrawInterceptReadout(dc, target, solution, center);
    }

    private Point ProjectTargetPoint(ComputedTarget target, Point center, double radius, double ownshipHeadingDeg)
    {
        var projected = ScopeProjection.ProjectToScope(
            center.X,
            center.Y,
            radius,
            target.RangeNm,
            target.BearingDegTrue,
            Settings!.SelectedRangeNm,
            ownshipHeadingDeg,
            Settings.OrientationMode == ScopeOrientationMode.HeadingUp);
        return new Point(projected.x, projected.y);
    }

    private Point ProjectGeographicPoint(
        double latitudeDeg,
        double longitudeDeg,
        Point center,
        double radius,
        double ownshipHeadingDeg,
        bool clampToRange)
    {
        var point = ScopeProjection.ProjectGeographicToScope(
            center.X,
            center.Y,
            radius,
            Picture!.Ownship.LatitudeDeg,
            Picture.Ownship.LongitudeDeg,
            latitudeDeg,
            longitudeDeg,
            Settings!.SelectedRangeNm,
            ownshipHeadingDeg,
            Settings.OrientationMode == ScopeOrientationMode.HeadingUp,
            clampToRange);
        return new Point(point.x, point.y);
    }

    private void DrawInterceptTargetHighlight(DrawingContext dc, Point point, double targetSymbolScale)
    {
        var scale = Math.Clamp(targetSymbolScale, 0.6, 1.8);
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(255, 210, 90)), 1.4);
        dc.DrawRectangle(null, pen, new Rect(point.X - 11 * scale, point.Y - 11 * scale, 22 * scale, 22 * scale));
    }

    private void DrawInterceptReadout(DrawingContext dc, ComputedTarget target, InterceptSolution solution, Point scopeCenter)
    {
        var text = solution.HasSolution
            ? $"INT {target.DisplayName} HDG {solution.HeadingDeg:000} TTI {FormatInterceptTime(solution.TimeSeconds)}"
            : $"INT {target.DisplayName} NO INT";
        DrawTopReadout(dc, text, scopeCenter.X + 150, 10.0, solution.HasSolution ? Color.FromRgb(255, 225, 120) : Color.FromRgb(255, 125, 125));
    }

    private void DrawInterceptLabel(DrawingContext dc, ComputedTarget target, InterceptSolution solution, Point point)
    {
        var altitude = target.RelativeAltitudeFt >= 0
            ? $"+{target.RelativeAltitudeFt:0}"
            : $"{target.RelativeAltitudeFt:0}";
        var line1 = $"INT {target.DisplayName}";
        var line2 = $"BRG {target.BearingDegTrue:000} RNG {target.RangeNm:0.0}";
        var line3 = $"ALT {altitude} FT";
        var line4 = solution.HasSolution
            ? $"HDG {solution.HeadingDeg:000} TTI {FormatInterceptTime(solution.TimeSeconds)}"
            : "NO INT";

        var lines = new[]
        {
            new LabelLine(line1, Color.FromRgb(255, 225, 120), 12, FontWeights.SemiBold),
            new LabelLine(line2, Color.FromRgb(255, 245, 190), 11, FontWeights.Normal),
            new LabelLine(line3, Color.FromRgb(255, 245, 190), 11, FontWeights.Normal),
            new LabelLine(line4, solution.HasSolution ? Color.FromRgb(255, 245, 190) : Color.FromRgb(255, 125, 125), 11, FontWeights.Normal)
        };
        var measured = MeasureLabel(lines);
        var rect = ClampToViewport(
            new Rect(point.X, point.Y, measured.Size.Width, measured.Size.Height),
            new Rect(2, 2, Math.Max(2, RenderSize.Width - 4), Math.Max(2, RenderSize.Height - 4)));
        DrawLabelBox(dc, measured.Lines, new LabelPlacement(rect, measured.Lines), TargetLabelBackgroundOpacity);
    }

    private static string FormatInterceptTime(double seconds)
    {
        var totalSeconds = Math.Max(0, (int)Math.Round(seconds));
        return $"{totalSeconds / 60:00}:{totalSeconds % 60:00}";
    }

    private static InterceptSolution CalculateInterceptSolution(OwnshipState ownship, ComputedTarget target)
    {
        var ownSpeedKt = ownship.SpeedKt ?? 0;
        var targetSpeedKt = target.SpeedKt ?? 0;
        var targetHeadingDeg = target.HeadingDeg ?? 0;
        if (ownSpeedKt <= 1 || target.RangeNm <= 0)
        {
            return InterceptSolution.None;
        }

        var targetBearingRad = target.BearingDegTrue * Math.PI / 180.0;
        var targetX = target.RangeNm * Math.Sin(targetBearingRad);
        var targetY = target.RangeNm * Math.Cos(targetBearingRad);
        var targetHeadingRad = targetHeadingDeg * Math.PI / 180.0;
        var targetVelocityX = targetSpeedKt * Math.Sin(targetHeadingRad);
        var targetVelocityY = targetSpeedKt * Math.Cos(targetHeadingRad);
        var a = (targetVelocityX * targetVelocityX) + (targetVelocityY * targetVelocityY) - (ownSpeedKt * ownSpeedKt);
        var b = 2 * ((targetX * targetVelocityX) + (targetY * targetVelocityY));
        var c = (targetX * targetX) + (targetY * targetY);
        var timeHours = SolvePositiveInterceptTimeHours(a, b, c);
        if (!timeHours.HasValue)
        {
            return InterceptSolution.None;
        }

        var interceptX = targetX + (targetVelocityX * timeHours.Value);
        var interceptY = targetY + (targetVelocityY * timeHours.Value);
        var interceptRange = Math.Sqrt((interceptX * interceptX) + (interceptY * interceptY));
        if (interceptRange <= 0.001)
        {
            return InterceptSolution.None;
        }

        var headingDeg = GeoMath.NormalizeDegrees(Math.Atan2(interceptX, interceptY) * 180.0 / Math.PI);
        var destination = GeoMath.DestinationPoint(
            ownship.LatitudeDeg,
            ownship.LongitudeDeg,
            headingDeg,
            interceptRange);

        return new InterceptSolution(
            true,
            headingDeg,
            timeHours.Value * 3600.0,
            destination.latitudeDeg,
            destination.longitudeDeg);
    }

    private static double? SolvePositiveInterceptTimeHours(double a, double b, double c)
    {
        const double epsilon = 1e-9;
        if (Math.Abs(a) < epsilon)
        {
            if (Math.Abs(b) < epsilon)
            {
                return null;
            }

            var linear = -c / b;
            return linear > 0 ? linear : null;
        }

        var discriminant = (b * b) - (4 * a * c);
        if (discriminant < 0)
        {
            return null;
        }

        var root = Math.Sqrt(discriminant);
        var t1 = (-b - root) / (2 * a);
        var t2 = (-b + root) / (2 * a);
        return new[] { t1, t2 }
            .Where(t => t > 0)
            .OrderBy(t => t)
            .Cast<double?>()
            .FirstOrDefault();
    }

    private static void DrawTargetHeading(
        DrawingContext dc,
        Point p,
        ComputedTarget target,
        Pen pen,
        double ownHeadingDeg,
        bool headingUp,
        double symbolScale)
    {
        if (!target.HeadingDeg.HasValue)
        {
            return;
        }

        var displayHeading = headingUp
            ? GeoMath.NormalizeDegrees(target.HeadingDeg.Value - ownHeadingDeg)
            : GeoMath.NormalizeDegrees(target.HeadingDeg.Value);
        var rad = displayHeading * System.Math.PI / 180.0;
        var length = 12.0 * symbolScale;
        var end = new Point(
            p.X + length * System.Math.Sin(rad),
            p.Y - length * System.Math.Cos(rad));
        dc.DrawLine(pen, p, end);
    }

    private void DrawTargetLabel(
        DrawingContext dc,
        ComputedTarget target,
        Point symbolPoint,
        LabelMode mode,
        ICollection<Rect> occupiedRects)
    {
        var lines = BuildTargetLabelLines(target, mode);
        if (lines.Count == 0)
        {
            return;
        }

        var placement = FindLabelPlacement(symbolPoint, lines, occupiedRects);
        var finalPlacement = ApplyLabelOffset(target.Id, placement);
        DrawLabelBox(dc, finalPlacement.Lines, finalPlacement, TargetLabelBackgroundOpacity);
        DrawLeaderLine(dc, symbolPoint, finalPlacement.Bounds);
        occupiedRects.Add(finalPlacement.Bounds);
        var currentOffset = new Vector(
            finalPlacement.Bounds.X - placement.Bounds.X,
            finalPlacement.Bounds.Y - placement.Bounds.Y);
        _hitLabels.Add(new HitLabel(target.Id, symbolPoint, placement.Bounds.TopLeft, finalPlacement.Bounds, currentOffset));
    }

    private static void DrawDiamond(DrawingContext dc, Point p, Pen pen, double symbolScale)
    {
        var size = 6 * symbolScale;
        var g = new StreamGeometry();
        using var ctx = g.Open();
        ctx.BeginFigure(new Point(p.X, p.Y - size), false, true);
        ctx.LineTo(new Point(p.X + size, p.Y), true, false);
        ctx.LineTo(new Point(p.X, p.Y + size), true, false);
        ctx.LineTo(new Point(p.X - size, p.Y), true, false);
        dc.DrawGeometry(null, pen, g);
    }

    private static SolidColorBrush GetBrush(ComputedTarget target) =>
        target.Category switch
        {
            TargetCategory.Friend => new SolidColorBrush(Color.FromRgb(120, 220, 255)),
            TargetCategory.Enemy => new SolidColorBrush(Color.FromRgb(255, 95, 95)),
            TargetCategory.Package => new SolidColorBrush(Color.FromRgb(80, 240, 170)),
            TargetCategory.Support => new SolidColorBrush(Color.FromRgb(240, 200, 100)),
            TargetCategory.Stale => new SolidColorBrush(Color.FromRgb(180, 180, 180)),
            _ => new SolidColorBrush(Color.FromRgb(255, 100, 100))
        };

    private void DrawText(DrawingContext dc, string text, double x, double y, Color color, double size)
    {
        DrawText(dc, text, x, y, color, size, FontWeights.Normal);
    }

    private void DrawCenteredText(DrawingContext dc, string text, double x, double y, Color color, double size, FontWeight weight)
    {
        var formatted = CreateFormattedText(text, color, size, weight);
        dc.DrawText(formatted, new Point(x - formatted.Width / 2.0, y - formatted.Height / 2.0));
    }

    private void DrawCenteredHaloText(DrawingContext dc, string text, double x, double y, Color color, double size, FontWeight weight)
    {
        var halo = CreateFormattedText(text, Color.FromArgb(245, 0, 8, 10), size, weight);
        var foreground = CreateFormattedText(text, color, size, weight);
        var point = new Point(x - foreground.Width / 2.0, y - foreground.Height / 2.0);
        DrawTextHalo(dc, halo, point);
        dc.DrawText(foreground, point);
    }

    private void DrawText(DrawingContext dc, string text, double x, double y, Color color, double size, FontWeight weight)
    {
        var formatted = CreateFormattedText(text, color, size, weight);
        dc.DrawText(formatted, new Point(x, y));
    }

    private void DrawBackedText(
        DrawingContext dc,
        string text,
        double x,
        double y,
        Color color,
        double size,
        FontWeight weight,
        double backgroundOpacity)
    {
        var formatted = CreateFormattedText(text, color, size, weight);
        var point = new Point(x, y);
        var opacity = Math.Clamp(backgroundOpacity, 0.0, 1.0);
        if (opacity > 0.01)
        {
            var background = new SolidColorBrush(WithScaledAlpha(190, 2, 7, 10, opacity));
            var border = new Pen(new SolidColorBrush(WithScaledAlpha(95, 35, 68, 65, opacity)), 0.7);
            dc.DrawRoundedRectangle(
                background,
                border,
                new Rect(point.X - 3, point.Y - 1, formatted.Width + 6, formatted.Height + 2),
                2,
                2);
        }

        dc.DrawText(formatted, point);
    }

    private void DrawHaloText(DrawingContext dc, string text, double x, double y, Color color, double size, FontWeight weight)
    {
        var halo = CreateFormattedText(text, Color.FromArgb(245, 0, 8, 10), size, weight);
        var foreground = CreateFormattedText(text, color, size, weight);
        var point = new Point(x, y);
        DrawTextHalo(dc, halo, point);
        dc.DrawText(foreground, point);
    }

    private static void DrawTextHalo(DrawingContext dc, FormattedText halo, Point point)
    {
        dc.DrawText(halo, new Point(point.X - 1, point.Y));
        dc.DrawText(halo, new Point(point.X + 1, point.Y));
        dc.DrawText(halo, new Point(point.X, point.Y - 1));
        dc.DrawText(halo, new Point(point.X, point.Y + 1));
    }

    private static IReadOnlyList<LabelLine> BuildTargetLabelLines(ComputedTarget target, LabelMode mode)
    {
        var altitude = AviationFormat.RelativeAltitudeHundreds(target.RelativeAltitudeFt);
        var min = $"{target.DisplayName} {target.RangeNm:0.0} {altitude}";
        var primary = new LabelLine(target.IsStale ? $"{min} STALE" : min, Colors.PaleTurquoise, 14, FontWeights.Normal);

        if (target.IsStale || mode == LabelMode.Minimal)
        {
            return [primary];
        }

        var aspect = target.HeadingDeg.HasValue
            ? AviationFormat.TargetAspect(target.HeadingDeg.Value, target.BearingDegTrue)
            : "---";
        var heading = target.HeadingDeg.HasValue ? $"{target.HeadingDeg.Value:000}" : "---";
        var closure = target.ClosureKt.HasValue ? $"{target.ClosureKt.Value:0}" : "---";
        var full = $"{aspect,-5} {heading} {closure}";
        var secondary = new LabelLine(full, Colors.LightGray, 12, FontWeights.Normal);
        return [primary, secondary];
    }

    private LabelPlacement FindLabelPlacement(
        Point symbolPoint,
        IReadOnlyList<LabelLine> lines,
        ICollection<Rect> occupiedRects)
    {
        var layout = MeasureLabel(lines);
        var viewport = new Rect(0, 0, RenderSize.Width, RenderSize.Height);
        var candidateOffsets = new[]
        {
            new Vector(LabelMargin, -14),
            new Vector(LabelMargin, 10),
            new Vector(-(layout.Size.Width + LabelMargin), -14),
            new Vector(-(layout.Size.Width + LabelMargin), 10),
            new Vector(LabelMargin, -(layout.Size.Height + 6)),
            new Vector(-(layout.Size.Width / 2.0), -(layout.Size.Height + 12)),
            new Vector(-(layout.Size.Width / 2.0), 12),
            new Vector(-(layout.Size.Width + LabelMargin), -(layout.Size.Height + 6))
        };

        for (var pass = 0; pass < 24; pass++)
        {
            var extraShift = pass * (layout.Size.Height + LabelSeparation);
            foreach (var baseOffset in candidateOffsets)
            {
                var offset = baseOffset;
                if (pass > 0)
                {
                    var direction = baseOffset.Y <= 0 ? -1.0 : 1.0;
                    offset.Y += direction * extraShift;
                }

                var candidate = new Rect(symbolPoint + offset, layout.Size);
                candidate = ClampToViewport(candidate, viewport);
                if (OverlapsAny(candidate, occupiedRects))
                {
                    continue;
                }

                return new LabelPlacement(candidate, layout.Lines);
            }
        }

        var fallback = ClampToViewport(new Rect(symbolPoint + new Vector(LabelMargin, -14), layout.Size), viewport);
        for (var i = 0; i < 64 && OverlapsAny(fallback, occupiedRects); i++)
        {
            fallback = ClampToViewport(
                new Rect(fallback.X, fallback.Bottom + LabelSeparation, fallback.Width, fallback.Height),
                viewport);
        }

        return new LabelPlacement(fallback, layout.Lines);
    }

    private LabelPlacement ApplyLabelOffset(string targetId, LabelPlacement placement)
    {
        var viewport = new Rect(0, 0, RenderSize.Width, RenderSize.Height);
        var offset = GetLabelOffset(targetId);
        var adjusted = new Rect(placement.Bounds.TopLeft + offset, placement.Bounds.Size);
        adjusted = ClampToViewport(adjusted, viewport);
        return placement with { Bounds = adjusted };
    }

    private MeasuredLabel MeasureLabel(IReadOnlyList<LabelLine> lines)
    {
        var measured = new List<MeasuredLabelLine>(lines.Count);
        var width = 0.0;
        var textHeight = 0.0;

        foreach (var line in lines)
        {
            var formatted = CreateFormattedText(line.Text, line.Color, line.Size, line.Weight);
            measured.Add(new MeasuredLabelLine(line, formatted));
            width = Math.Max(width, formatted.Width);
            textHeight += formatted.Height;
        }

        if (measured.Count > 1)
        {
            textHeight += LabelLineGap * (measured.Count - 1);
        }

        var totalSize = new Size(
            width + (LabelPaddingX * 2),
            textHeight + (LabelPaddingY * 2));

        return new MeasuredLabel(totalSize, measured);
    }

    private static void DrawLabelBox(
        DrawingContext dc,
        IReadOnlyList<MeasuredLabelLine> lines,
        LabelPlacement placement,
        double backgroundOpacity)
    {
        var opacity = Math.Clamp(backgroundOpacity, 0.0, 1.0);
        if (opacity > 0.01)
        {
            var background = new SolidColorBrush(WithScaledAlpha(168, 3, 10, 16, opacity));
            var border = new Pen(new SolidColorBrush(WithScaledAlpha(96, 110, 180, 170, opacity)), 1);
            dc.DrawRoundedRectangle(background, border, placement.Bounds, 3, 3);
        }

        var y = placement.Bounds.Y + LabelPaddingY;
        foreach (var line in lines)
        {
            dc.DrawText(line.Formatted, new Point(placement.Bounds.X + LabelPaddingX, y));
            y += line.Formatted.Height + LabelLineGap;
        }
    }

    private static void DrawLeaderLine(DrawingContext dc, Point symbolPoint, Rect labelRect)
    {
        var anchor = new Point(
            Math.Clamp(symbolPoint.X, labelRect.Left, labelRect.Right),
            Math.Clamp(symbolPoint.Y, labelRect.Top, labelRect.Bottom));

        if ((anchor - symbolPoint).Length < 2)
        {
            return;
        }

        var pen = new Pen(new SolidColorBrush(Color.FromArgb(110, 120, 210, 200)), 0.8);
        dc.DrawLine(pen, symbolPoint, anchor);
    }

    private static Rect ClampToViewport(Rect rect, Rect viewport)
    {
        var x = Math.Clamp(rect.X, viewport.Left, Math.Max(viewport.Left, viewport.Right - rect.Width));
        var y = Math.Clamp(rect.Y, viewport.Top, Math.Max(viewport.Top, viewport.Bottom - rect.Height));
        return new Rect(x, y, rect.Width, rect.Height);
    }

    private static bool OverlapsAny(Rect candidate, ICollection<Rect> occupiedRects)
    {
        foreach (var rect in occupiedRects)
        {
            if (candidate.IntersectsWith(rect))
            {
                return true;
            }
        }

        return false;
    }

    private FormattedText CreateFormattedText(string text, Color color, double size, FontWeight weight)
    {
        return new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Consolas"), FontStyles.Normal, weight, FontStretches.Normal),
            size,
            new SolidColorBrush(color),
            GetPixelsPerDip());
    }

    private double GetPixelsPerDip()
    {
        try
        {
            return VisualTreeHelper.GetDpi(this).PixelsPerDip;
        }
        catch
        {
            return 1.0;
        }
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        var pos = e.GetPosition(this);
        var labelHit = _hitLabels.LastOrDefault(t => t.Bounds.Contains(pos));
        if (labelHit is not null)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                if (InterceptSelectionArmed)
                {
                    TargetClicked?.Invoke(this, new ScopeTargetClickEventArgs(labelHit.TargetId, e.ChangedButton));
                    e.Handled = true;
                    return;
                }

                CaptureMouse();
                _dragState = new DragState(labelHit.TargetId, pos, labelHit.BaseTopLeft, labelHit.CurrentOffset);
                e.Handled = true;
                return;
            }

            if (e.ChangedButton is MouseButton.Middle or MouseButton.Right)
            {
                TargetClicked?.Invoke(this, new ScopeTargetClickEventArgs(labelHit.TargetId, e.ChangedButton));
                e.Handled = true;
                return;
            }
        }

        var hit = _hitTargets
            .Select(t => (t.id, distance: (t.point - pos).Length))
            .OrderBy(t => t.distance)
            .FirstOrDefault();

        var targetHitRadius = 14 * Math.Clamp(Settings?.TargetSymbolScale ?? 1.0, 0.6, 1.8);
        if (!string.IsNullOrWhiteSpace(hit.id) && hit.distance <= targetHitRadius)
        {
            TargetClicked?.Invoke(this, new ScopeTargetClickEventArgs(hit.id, e.ChangedButton));
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragState is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragState is null || e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        var offset = GetLabelOffset(_dragState.TargetId);
        LabelMoved?.Invoke(this, new ScopeLabelMovedEventArgs(_dragState.TargetId, offset.X, offset.Y));
        _dragState = null;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    private bool IsLabelHidden(string targetId) =>
        ManualTargetMetadata is not null &&
        ManualTargetMetadata.TryGetValue(targetId, out var metadata) &&
        metadata.LabelHidden;

    private Vector GetLabelOffset(string targetId)
    {
        if (_dragState is not null && string.Equals(_dragState.TargetId, targetId, StringComparison.OrdinalIgnoreCase))
        {
            var mousePosition = Mouse.GetPosition(this);
            return _dragState.StartOffset + (mousePosition - _dragState.StartPosition);
        }

        if (ManualTargetMetadata is not null &&
            ManualTargetMetadata.TryGetValue(targetId, out var metadata))
        {
            return new Vector(metadata.LabelOffsetX, metadata.LabelOffsetY);
        }

        return new Vector();
    }

    private sealed record LabelLine(string Text, Color Color, double Size, FontWeight Weight);
    private sealed record MeasuredLabelLine(LabelLine Line, FormattedText Formatted);
    private sealed record MeasuredLabel(Size Size, IReadOnlyList<MeasuredLabelLine> Lines);
    private sealed record LabelPlacement(Rect Bounds, IReadOnlyList<MeasuredLabelLine> Lines);
    private sealed record InterceptSolution(bool HasSolution, double HeadingDeg, double TimeSeconds, double LatitudeDeg, double LongitudeDeg)
    {
        public static InterceptSolution None { get; } = new(false, 0, 0, 0, 0);
    }

    private sealed record HitLabel(string TargetId, Point SymbolPoint, Point BaseTopLeft, Rect Bounds, Vector CurrentOffset);
    private sealed record DragState(string TargetId, Point StartPosition, Point BaseTopLeft, Vector StartOffset);
}
