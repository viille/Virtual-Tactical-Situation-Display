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
        DrawRings(dc, center, radius, Picture.Ownship.HeadingDeg);
        DrawOptionalOverlay(() => DrawAirspaces(dc, center, radius));
        DrawOptionalOverlay(() => DrawBullseye(dc, center, radius));
        DrawOwnship(dc, center, radius, Picture.Ownship.HeadingDeg);
        DrawTargets(dc, center, radius);
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

        var rect = new Rect(0, 0, RenderSize.Width, RenderSize.Height);
        var brush = new LinearGradientBrush(
            Color.FromRgb(4, 12, 18),
            Color.FromRgb(8, 26, 28),
            new Point(0, 0),
            new Point(1, 1));
        dc.DrawRectangle(brush, null, rect);
    }

    private void DrawRings(DrawingContext dc, Point center, double radius, double ownHeadingDeg)
    {
        if (Settings is null || !Settings.ShowRangeRings)
        {
            return;
        }

        var pen = new Pen(new SolidColorBrush(Color.FromArgb(150, 90, 160, 140)), 1);
        var ringCount = 4;
        for (var i = 1; i <= ringCount; i++)
        {
            var ringRadius = radius * i / ringCount;
            dc.DrawEllipse(null, pen, center, ringRadius, ringRadius);
            var label = $"{Settings.SelectedRangeNm * i / ringCount:0} NM";
            DrawText(dc, label, center.X + 5, center.Y - ringRadius - 16, Colors.LightGreen, 11);
        }

        DrawCompassRose(dc, center, radius, ownHeadingDeg);
    }

    private void DrawCompassRose(DrawingContext dc, Point center, double radius, double ownHeadingDeg)
    {
        if (Settings is null)
        {
            return;
        }

        var radialPen = new Pen(new SolidColorBrush(Color.FromArgb(32, 150, 220, 205)), 0.8);

        var directions = new (string label, double bearingDeg)[]
        {
            ("360", 0),
            ("045", 45),
            ("090", 90),
            ("135", 135),
            ("180", 180),
            ("225", 225),
            ("270", 270),
            ("315", 315)
        };

        foreach (var direction in directions)
        {
            var displayBearing = Settings.OrientationMode == ScopeOrientationMode.HeadingUp
                ? GeoMath.NormalizeDegrees(direction.bearingDeg - ownHeadingDeg)
                : direction.bearingDeg;
            var rad = displayBearing * System.Math.PI / 180.0;
            var lineEndRadius = radius - 8;
            var textRadius = radius + 14;
            var lineEnd = new Point(
                center.X + lineEndRadius * System.Math.Sin(rad),
                center.Y - lineEndRadius * System.Math.Cos(rad));
            var x = center.X + textRadius * System.Math.Sin(rad);
            var y = center.Y - textRadius * System.Math.Cos(rad);
            dc.DrawLine(radialPen, center, lineEnd);
            DrawCenteredText(dc, direction.label, x, y, Colors.LightSeaGreen, 12, FontWeights.SemiBold);
        }
    }

    private void DrawOwnship(DrawingContext dc, Point center, double radius, double headingDeg)
    {
        var triangle = new StreamGeometry();
        using (var ctx = triangle.Open())
        {
            ctx.BeginFigure(new Point(center.X, center.Y - 10), true, true);
            ctx.LineTo(new Point(center.X - 8, center.Y + 8), true, false);
            ctx.LineTo(new Point(center.X + 8, center.Y + 8), true, false);
        }

        var rotation = Settings?.OrientationMode == ScopeOrientationMode.HeadingUp ? 0 : headingDeg;
        dc.PushTransform(new RotateTransform(rotation, center.X, center.Y));
        dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(170, 255, 220)), new Pen(Brushes.Black, 1), triangle);
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(170, 255, 220)), 1.5), center, new Point(center.X, center.Y - radius));
        dc.Pop();
    }

    private void DrawAirspaces(DrawingContext dc, Point center, double radius)
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
        var inactivePen = new Pen(new SolidColorBrush(WithScaledAlpha(70, 110, 180, 170, opacity)), 0.8);
        var inactiveFill = new SolidColorBrush(WithScaledAlpha(12, 110, 180, 170, opacity));

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, RenderSize.Width, RenderSize.Height)));
        try
        {
            foreach (var airspace in Airspaces)
            {
                if (Settings.ShowOnlyActiveAirspaceBoundaries && !airspace.IsActive)
                {
                    continue;
                }

                foreach (var polygon in airspace.Polygons)
                {
                    var projected = ProjectAirspacePolygon(polygon, center, radius);
                    if (projected.Count < 2 || !IntersectsViewport(projected))
                    {
                        continue;
                    }

                    var geometry = BuildAirspaceGeometry(projected);
                    dc.DrawGeometry(airspace.IsActive ? activeFill : inactiveFill, airspace.IsActive ? pen : inactivePen, geometry);
                }

                if (airspace.IsActive && !string.IsNullOrWhiteSpace(airspace.Name))
                {
                    DrawAirspaceLabel(dc, airspace, center, radius, opacity);
                }
            }
        }
        finally
        {
            dc.Pop();
        }
    }

    private IReadOnlyList<Point> ProjectAirspacePolygon(AirspacePolygon polygon, Point center, double radius)
    {
        if (Picture is null || Settings is null)
        {
            return [];
        }

        var projected = new List<Point>(polygon.Exterior.Count);
        foreach (var coordinate in polygon.Exterior)
        {
            var range = GeoMath.DistanceNm(
                Picture.Ownship.LatitudeDeg,
                Picture.Ownship.LongitudeDeg,
                coordinate.LatitudeDeg,
                coordinate.LongitudeDeg);
            var bearing = GeoMath.InitialBearingDeg(
                Picture.Ownship.LatitudeDeg,
                Picture.Ownship.LongitudeDeg,
                coordinate.LatitudeDeg,
                coordinate.LongitudeDeg);
            var point = ScopeProjection.ProjectToScope(
                center.X,
                center.Y,
                radius,
                range,
                bearing,
                Settings.SelectedRangeNm,
                Picture.Ownship.HeadingDeg,
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

    private void DrawAirspaceLabel(DrawingContext dc, AirspaceArea airspace, Point center, double radius, double opacity)
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

        var bearing = GeoMath.InitialBearingDeg(Picture.Ownship.LatitudeDeg, Picture.Ownship.LongitudeDeg, latitude, longitude);
        var point = ScopeProjection.ProjectToScope(
            center.X,
            center.Y,
            radius,
            range,
            bearing,
            Settings.SelectedRangeNm,
            Picture.Ownship.HeadingDeg,
            Settings.OrientationMode == ScopeOrientationMode.HeadingUp);
        DrawCenteredText(dc, BuildAirspaceLabelText(airspace), point.x, point.y, WithScaledAlpha(255, 247, 200, 115, opacity), 11, FontWeights.SemiBold);
    }

    private static string BuildAirspaceLabelText(AirspaceArea airspace)
    {
        if (airspace.IsActive &&
            airspace.ActiveLowerAltitudeFt.HasValue &&
            airspace.ActiveUpperAltitudeFt.HasValue)
        {
            return $"{airspace.Name} {airspace.ActiveLowerAltitudeFt.Value}-{airspace.ActiveUpperAltitudeFt.Value}";
        }

        var lower = airspace.LowerFlightLevel.HasValue ? $"FL{airspace.LowerFlightLevel.Value:000}" : string.Empty;
        var upper = airspace.UpperFlightLevel.HasValue ? $"FL{airspace.UpperFlightLevel.Value:000}" : string.Empty;
        return string.IsNullOrWhiteSpace(lower) || string.IsNullOrWhiteSpace(upper)
            ? airspace.Name
            : $"{airspace.Name} {lower}-{upper}";
    }

    private static Color WithScaledAlpha(byte alpha, byte red, byte green, byte blue, double opacity) =>
        Color.FromArgb((byte)Math.Clamp(alpha * opacity, 0, 255), red, green, blue);

    private void DrawBullseye(DrawingContext dc, Point center, double radius)
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
        var point = ScopeProjection.ProjectToScope(
            center.X,
            center.Y,
            radius,
            range,
            bearing,
            Settings.SelectedRangeNm,
            Picture.Ownship.HeadingDeg,
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

    private void DrawTargets(DrawingContext dc, Point center, double radius)
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
                Picture.Ownship.HeadingDeg,
                Settings.OrientationMode == ScopeOrientationMode.HeadingUp);

            if (Settings.TrailsEnabled && !Settings.Declutter)
            {
                DrawTrail(dc, target, center, radius);
            }

            var projectedPoint = new Point(projected.x, projected.y);
            DrawTargetSymbol(
                dc,
                projectedPoint,
                target,
                Picture.Ownship.HeadingDeg,
                Settings.OrientationMode == ScopeOrientationMode.HeadingUp);
            _hitTargets.Add((target.Id, projectedPoint));
            var effectiveLabelMode = Settings.Declutter ? LabelMode.Minimal : Settings.LabelMode;
            if (effectiveLabelMode != LabelMode.Off && !IsLabelHidden(target.Id))
            {
                DrawTargetLabel(dc, target, projectedPoint, effectiveLabelMode, labelRects);
            }
        }
    }

    private void DrawTrail(DrawingContext dc, ComputedTarget target, Point center, double radius)
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
            var bearing = GeoMath.InitialBearingDeg(Picture.Ownship.LatitudeDeg, Picture.Ownship.LongitudeDeg, point.LatitudeDeg, point.LongitudeDeg);
            var projection = ScopeProjection.ProjectToScope(
                center.X,
                center.Y,
                radius,
                range,
                bearing,
                Settings.SelectedRangeNm,
                Picture.Ownship.HeadingDeg,
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
        bool headingUp)
    {
        var brush = GetBrush(target);
        var dim = target.IsStale ? 0.4 : 1.0;
        brush = brush.Clone();
        brush.Opacity = dim;
        var pen = new Pen(brush, 1.5);
        DrawTargetHeading(dc, p, target, pen, ownHeadingDeg, headingUp);

        switch (target.Category)
        {
            case TargetCategory.Friend:
                dc.DrawEllipse(null, pen, p, 5, 5);
                break;
            case TargetCategory.Enemy:
                dc.DrawLine(pen, new Point(p.X - 5, p.Y - 5), new Point(p.X + 5, p.Y + 5));
                dc.DrawLine(pen, new Point(p.X - 5, p.Y + 5), new Point(p.X + 5, p.Y - 5));
                break;
            case TargetCategory.Package:
                DrawDiamond(dc, p, pen);
                break;
            case TargetCategory.Support:
                dc.DrawRectangle(null, pen, new Rect(p.X - 4, p.Y - 4, 8, 8));
                break;
            default:
                dc.DrawEllipse(brush, null, p, 2.5, 2.5);
                break;
        }
    }

    private static void DrawTargetHeading(
        DrawingContext dc,
        Point p,
        ComputedTarget target,
        Pen pen,
        double ownHeadingDeg,
        bool headingUp)
    {
        if (!target.HeadingDeg.HasValue)
        {
            return;
        }

        var displayHeading = headingUp
            ? GeoMath.NormalizeDegrees(target.HeadingDeg.Value - ownHeadingDeg)
            : GeoMath.NormalizeDegrees(target.HeadingDeg.Value);
        var rad = displayHeading * System.Math.PI / 180.0;
        var length = 12.0;
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
        DrawLabelBox(dc, finalPlacement.Lines, finalPlacement);
        DrawLeaderLine(dc, symbolPoint, finalPlacement.Bounds);
        occupiedRects.Add(finalPlacement.Bounds);
        var currentOffset = new Vector(
            finalPlacement.Bounds.X - placement.Bounds.X,
            finalPlacement.Bounds.Y - placement.Bounds.Y);
        _hitLabels.Add(new HitLabel(target.Id, symbolPoint, placement.Bounds.TopLeft, finalPlacement.Bounds, currentOffset));
    }

    private static void DrawDiamond(DrawingContext dc, Point p, Pen pen)
    {
        var g = new StreamGeometry();
        using var ctx = g.Open();
        ctx.BeginFigure(new Point(p.X, p.Y - 6), false, true);
        ctx.LineTo(new Point(p.X + 6, p.Y), true, false);
        ctx.LineTo(new Point(p.X, p.Y + 6), true, false);
        ctx.LineTo(new Point(p.X - 6, p.Y), true, false);
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

    private void DrawText(DrawingContext dc, string text, double x, double y, Color color, double size, FontWeight weight)
    {
        var formatted = CreateFormattedText(text, color, size, weight);
        dc.DrawText(formatted, new Point(x, y));
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
        var full = $"ASP {aspect,-5} HDG {heading} CLS {closure}";
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

    private static void DrawLabelBox(DrawingContext dc, IReadOnlyList<MeasuredLabelLine> lines, LabelPlacement placement)
    {
        var background = new SolidColorBrush(Color.FromArgb(168, 3, 10, 16));
        var border = new Pen(new SolidColorBrush(Color.FromArgb(96, 110, 180, 170)), 1);
        dc.DrawRoundedRectangle(background, border, placement.Bounds, 3, 3);

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
                CaptureMouse();
                _dragState = new DragState(labelHit.TargetId, pos, labelHit.BaseTopLeft, labelHit.CurrentOffset);
                e.Handled = true;
                return;
            }

            if (e.ChangedButton == MouseButton.Middle)
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

        if (!string.IsNullOrWhiteSpace(hit.id) && hit.distance <= 14)
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
    private sealed record HitLabel(string TargetId, Point SymbolPoint, Point BaseTopLeft, Rect Bounds, Vector CurrentOffset);
    private sealed record DragState(string TargetId, Point StartPosition, Point BaseTopLeft, Vector StartOffset);
}
