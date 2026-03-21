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
    private readonly List<(string id, Point point)> _hitTargets = [];

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

    public event EventHandler<ScopeTargetClickEventArgs>? TargetClicked;

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        _hitTargets.Clear();
        DrawBackground(dc);
        if (Picture is null || Settings is null)
        {
            return;
        }

        var center = new Point(RenderSize.Width / 2.0, RenderSize.Height / 2.0);
        var radius = System.Math.Min(RenderSize.Width, RenderSize.Height) * 0.45;
        DrawRings(dc, center, radius, Picture.Ownship.HeadingDeg);
        DrawOwnship(dc, center, radius, Picture.Ownship.HeadingDeg);
        DrawTargets(dc, center, radius);
    }

    private void DrawBackground(DrawingContext dc)
    {
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

        foreach (var target in visibleTargets)
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
            if (effectiveLabelMode != LabelMode.Off)
            {
                DrawTargetLabel(dc, target, projected.x + 8, projected.y - 14, effectiveLabelMode);
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
        foreach (var point in target.History)
        {
            var range = GeoMath.DistanceNm(Picture.Ownship.LatitudeDeg, Picture.Ownship.LongitudeDeg, point.LatitudeDeg, point.LongitudeDeg);
            if (range > Settings.SelectedRangeNm)
            {
                continue;
            }

            var bearing = GeoMath.InitialBearingDeg(Picture.Ownship.LatitudeDeg, Picture.Ownship.LongitudeDeg, point.LatitudeDeg, point.LongitudeDeg);
            var projection = ScopeProjection.ProjectToScope(
                center.X,
                center.Y,
                radius,
                range,
                bearing,
                Settings.SelectedRangeNm,
                Picture.Ownship.HeadingDeg,
                Settings.OrientationMode == ScopeOrientationMode.HeadingUp);
            var current = new Point(projection.x, projection.y);
            if (previous is not null)
            {
                dc.DrawLine(pen, previous.Value, current);
            }

            previous = current;
        }
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

    private static void DrawTargetLabel(DrawingContext dc, ComputedTarget target, double x, double y, LabelMode mode)
    {
        var altitude = AviationFormat.RelativeAltitudeHundreds(target.RelativeAltitudeFt);
        var min = $"{target.DisplayName} {target.RangeNm:0.0} {altitude}";
        var aspect = target.HeadingDeg.HasValue
            ? AviationFormat.TargetAspect(target.HeadingDeg.Value, target.BearingDegTrue)
            : "---";
        var heading = target.HeadingDeg.HasValue ? $"{target.HeadingDeg.Value:000}" : "---";
        var closure = target.ClosureKt.HasValue ? $"{target.ClosureKt.Value:0}" : "---";
        var full = $"ASP {aspect,-5} HDG {heading} CLS {closure}";
        DrawText(dc, target.IsStale ? $"{min} STALE" : min, x, y, Colors.PaleTurquoise, 14);
        if (target.IsStale || mode == LabelMode.Minimal)
        {
            return;
        }

        DrawText(dc, full, x, y + 15, Colors.LightGray, 12);
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

    private static void DrawText(DrawingContext dc, string text, double x, double y, Color color, double size)
    {
        DrawText(dc, text, x, y, color, size, FontWeights.Normal);
    }

    private static void DrawCenteredText(DrawingContext dc, string text, double x, double y, Color color, double size, FontWeight weight)
    {
        var formatted = CreateFormattedText(text, color, size, weight);
        dc.DrawText(formatted, new Point(x - formatted.Width / 2.0, y - formatted.Height / 2.0));
    }

    private static void DrawText(DrawingContext dc, string text, double x, double y, Color color, double size, FontWeight weight)
    {
        var formatted = CreateFormattedText(text, color, size, weight);
        dc.DrawText(formatted, new Point(x, y));
    }

    private static FormattedText CreateFormattedText(string text, Color color, double size, FontWeight weight)
    {
        return new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Consolas"), FontStyles.Normal, weight, FontStretches.Normal),
            size,
            new SolidColorBrush(color),
            1.0);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        var pos = e.GetPosition(this);
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
}
