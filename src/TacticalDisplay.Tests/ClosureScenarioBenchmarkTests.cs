using TacticalDisplay.Core.Models;
using TacticalDisplay.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace TacticalDisplay.Tests;

/// <summary>Deterministic estimator comparison across straight flight, maneuvers and data-quality cases.</summary>
public sealed class ClosureScenarioBenchmarkTests(ITestOutputHelper output)
{
    private const double DurationSeconds = 12;
    private const double IntegrationStepSeconds = 0.025;

    [Fact]
    public void ClosureBenchmark_ReportsTruthPositionVectorAndPublishedEstimatesAcrossScenarios()
    {
        var scenarios = CreateScenarios();
        output.WriteLine("scenario | T50/T90 final [fixed4s] | peak error P/V/F kt | steady MAE P/V/F kt | steady jitter P/V/F kt | source jump kt | outlier deviation/recovery kt/s");
        foreach (var scenario in scenarios)
        {
            var samples = Run(scenario);
            var peakPositionError = PeakError(samples, static sample => sample.Position);
            var peakVectorError = PeakError(samples, static sample => sample.Vector);
            var peakFinalError = PeakError(samples, static sample => sample.Final);
            var steadyPositionMae = MeanError(samples, static sample => sample.Position);
            var steadyVectorMae = MeanError(samples, static sample => sample.Vector);
            var steadyFinalMae = MeanError(samples, static sample => sample.Final);
            var steadyPositionJitter = Jitter(samples, static sample => sample.Position);
            var steadyVectorJitter = Jitter(samples, static sample => sample.Vector);
            var steadyFinalJitter = Jitter(samples, static sample => sample.Final);
            var transitionJump = samples.Zip(samples.Skip(1), (before, after) => (before, after))
                .Where(pair => pair.before.Source != pair.after.Source && pair.before.Final.HasValue && pair.after.Final.HasValue)
                .Select(pair => System.Math.Abs((pair.after.Final!.Value - pair.before.Final!.Value) - (pair.after.Truth - pair.before.Truth)))
                .DefaultIfEmpty(0).Max();
            var t50 = ResponseTime(samples, scenario.EventTime, 0.5);
            var t90 = ResponseTime(samples, scenario.EventTime, 0.9);
            var oldT50 = scenario.Name == "closure-step-100-500" ? BaselineResponseTime(samples, scenario.EventTime, 0.5) : null;
            var oldT90 = scenario.Name == "closure-step-100-500" ? BaselineResponseTime(samples, scenario.EventTime, 0.9) : null;
            var outlierDeviation = scenario.BadPositionSample
                ? samples.Where(sample => sample.Time is >= 7.75 and <= 8.5).Select(sample => System.Math.Abs(sample.Final.GetValueOrDefault() - sample.Truth)).DefaultIfEmpty(0).Max()
                : 0;
            var outlierRecovery = scenario.BadPositionSample ? RecoveryTime(samples, 8, 20) : 0;

            var responseText = $"{Format(t50)}/{Format(t90)}" + (oldT50.HasValue ? $" [{Format(oldT50)}/{Format(oldT90)}]" : string.Empty);
            output.WriteLine($"{scenario.Name} | {responseText} s | {peakPositionError:F1}/{peakVectorError:F1}/{peakFinalError:F1} | " +
                $"{steadyPositionMae:F1}/{steadyVectorMae:F1}/{steadyFinalMae:F1} | {steadyPositionJitter:F1}/{steadyVectorJitter:F1}/{steadyFinalJitter:F1} | " +
                $"{transitionJump:F1} | {outlierDeviation:F1}/{outlierRecovery:F2}");
            var latest = samples.Last();
            output.WriteLine($"  final truth={latest.Truth:F1} position={Format(latest.Position)} vector={Format(latest.Vector)} published={Format(latest.Final)} source={latest.Source}");
        }

        Assert.Equal(14, scenarios.Count);
        var startup = Run(scenarios.Single(scenario => scenario.Name == "startup-vector-before-history")).First();
        Assert.Null(startup.Position);
        Assert.NotNull(startup.Vector);
        Assert.Equal("vector", startup.Source);
        var disagreement = Run(scenarios.Single(scenario => scenario.Name == "vector-position-disagreement")).Last();
        Assert.True(disagreement.Position.HasValue && disagreement.Vector.HasValue && disagreement.Final.HasValue);
        Assert.True(System.Math.Abs(disagreement.Position.Value - disagreement.Vector.Value) > 500);
        Assert.InRange(System.Math.Abs(disagreement.Final!.Value - disagreement.Position!.Value), 0, 5);
        var alignmentRecovery = Run(scenarios.Single(scenario => scenario.Name == "temporary-alignment-loss"));
        Assert.Contains(alignmentRecovery, sample => sample.Alignment == "unavailable" && sample.RangeDecision == "rejected-alignment-unavailable");
        Assert.Contains(alignmentRecovery, sample => sample.Alignment == "exact" && sample.RangeDecision == "accepted-aligned");
        Assert.All(scenarios.Select(Run), samples => Assert.NotEmpty(samples));
    }

    private static List<Sample> Run(Scenario scenario)
    {
        var repository = new TrafficRepository();
        var settings = new TacticalDisplaySettings { SelectedRangeNm = 100 };
        var classification = new ClassificationConfig();
        var start = DateTimeOffset.UnixEpoch;
        var samples = new List<Sample>();
        foreach (var time in SampleTimes(scenario.IrregularTimestamps))
        {
            var ownTime = System.Math.Max(0, time - (scenario.OwnshipLag?.Invoke(time) ?? 0));
            var ownPose = PositionAt(ownTime, scenario.OwnshipMotion);
            var ownMotion = scenario.OwnshipMotion(ownTime);
            var ownship = new OwnshipState("OWN", ownPose.NorthNm / 60, ownPose.EastNm / 60, 5000,
                ownMotion.ReportedTrack ?? ownMotion.Track, ownMotion.Speed,
                start.AddSeconds(time - (scenario.OwnshipLag?.Invoke(time) ?? 0)),
                GroundTrackDeg: ownMotion.ReportedTrack ?? ownMotion.Track);

            var targetPose = PositionAt(time, scenario.TargetMotion, initialNorthNm: 20);
            if (scenario.BadPositionSample && System.Math.Abs(time - 8) < 0.001) targetPose = targetPose with { EastNm = targetPose.EastNm + 0.2 };
            var targetMotion = scenario.TargetMotion(time);
            var targetTime = start.AddSeconds(time);
            var contact = new TrafficContactState("T1", "BENCH1", targetPose.NorthNm / 60, targetPose.EastNm / 60,
                5000, targetMotion.ReportedTrack ?? targetMotion.Track, targetMotion.Speed, targetTime,
                GroundTrackDeg: targetMotion.ReportedTrack ?? targetMotion.Track);
            repository.ApplySnapshot(new TrafficSnapshot(ownship, [contact], targetTime), classification, settings);
            var computed = repository.BuildPicture(settings).Targets.SingleOrDefault();
            var tracked = repository.GetTrackedContact("T1")!;
            samples.Add(new Sample(time, TruthClosure(time, scenario), tracked.RelativeRangeNm, tracked.PositionClosureKt,
                tracked.VectorClosureKt, computed?.ClosureKt, tracked.ClosureSource, tracked.AlignmentMethod, tracked.RangeSampleDecision));
        }

        return samples;
    }

    private static IReadOnlyList<Scenario> CreateScenarios()
    {
        static Motion Stationary(double _) => new(0, 0);
        static Motion North(double _) => new(0, 300);
        static Motion South300(double _) => new(180, 300);
        return
        [
            new("constant-closure", Stationary, South300),
            new("closure-step-100-500", Stationary, time => new(180, time < 4 ? 100 : 500), EventTime: 4),
            new("ownship-acceleration", time => new(0, time < 4 ? 100 : 500), Stationary, EventTime: 4),
            new("target-acceleration", Stationary, time => new(180, time < 4 ? 100 : 500), EventTime: 4),
            new("ownship-turn-90", time => new(time < 4 ? 0 : 90, 300), Stationary, EventTime: 4),
            new("target-turn-90", Stationary, time => new(time < 4 ? 180 : 90, 300), EventTime: 4),
            new("gradual-turn", time => new(System.Math.Clamp((time - 4) * 22.5, 0, 90), 300), Stationary, EventTime: 4),
            new("vector-position-agreement", Stationary, South300),
            new("vector-position-disagreement", Stationary, time => new(180, 300, time >= 4 ? 0 : null), EventTime: 4),
            new("single-bad-position", Stationary, South300, BadPositionSample: true),
            new("irregular-timestamps", Stationary, South300, IrregularTimestamps: true),
            new("temporary-alignment-loss", Stationary, South300, OwnshipLag: time => time < 6.5 ? 0.75 : 0),
            new("startup-vector-before-history", Stationary, South300),
            new("vector-position-transition", North, time => new(180, time < 2 ? 100 : 300), EventTime: 2)
        ];
    }

    private static IEnumerable<double> SampleTimes(bool irregular)
    {
        yield return 0;
        var time = 0.0;
        var index = 0;
        while (time < DurationSeconds)
        {
            time = System.Math.Min(DurationSeconds, time + (irregular && index % 4 == 2 ? 0.75 : 0.25));
            yield return time;
            index++;
        }
    }

    private static Position PositionAt(double time, Func<double, Motion> motion, double initialNorthNm = 0)
    {
        var north = initialNorthNm;
        var east = 0.0;
        var steps = (int)System.Math.Ceiling(System.Math.Max(0, time) / IntegrationStepSeconds);
        for (var index = 0; index < steps; index++)
        {
            var elapsed = index * IntegrationStepSeconds;
            var state = motion(elapsed);
            var distance = state.Speed * IntegrationStepSeconds / 3600.0;
            var radians = state.Track * System.Math.PI / 180;
            north += System.Math.Cos(radians) * distance;
            east += System.Math.Sin(radians) * distance;
        }

        return new Position(north, east);
    }

    private static double TruthClosure(double time, Scenario scenario)
    {
        const double delta = 0.05;
        var beforeTime = System.Math.Max(0, time - delta);
        var before = RelativeRange(beforeTime, scenario);
        var after = RelativeRange(time + delta, scenario);
        return -(after - before) / (time + delta - beforeTime) * 3600;
    }

    private static double RelativeRange(double time, Scenario scenario)
    {
        var ownship = PositionAt(System.Math.Max(0, time), scenario.OwnshipMotion);
        var target = PositionAt(System.Math.Max(0, time), scenario.TargetMotion, initialNorthNm: 20);
        return System.Math.Sqrt(System.Math.Pow(target.NorthNm - ownship.NorthNm, 2) + System.Math.Pow(target.EastNm - ownship.EastNm, 2));
    }

    private static double? ResponseTime(IReadOnlyList<Sample> samples, double? eventTime, double fraction)
    {
        if (!eventTime.HasValue) return null;
        var before = samples.LastOrDefault(sample => sample.Time < eventTime.Value)?.Truth;
        var after = samples.LastOrDefault()?.Truth;
        if (!before.HasValue || !after.HasValue || System.Math.Abs(after.Value - before.Value) < 20) return null;
        var threshold = before.Value + (after.Value - before.Value) * fraction;
        var reached = samples.FirstOrDefault(sample => sample.Time >= eventTime.Value &&
            (after.Value >= before.Value ? sample.Final >= threshold : sample.Final <= threshold));
        return reached?.Final.HasValue == true ? reached.Time - eventTime.Value : null;
    }

    private static double? BaselineResponseTime(IReadOnlyList<Sample> samples, double? eventTime, double fraction)
    {
        if (!eventTime.HasValue) return null;
        var values = samples.Select((sample, index) => (sample.Time, Value: BaselineFixedFourSecond(samples.Take(index + 1).ToArray()))).ToArray();
        var before = samples.LastOrDefault(sample => sample.Time < eventTime.Value)?.Truth;
        var after = samples.LastOrDefault()?.Truth;
        if (!before.HasValue || !after.HasValue) return null;
        var threshold = before.Value + (after.Value - before.Value) * fraction;
        var reached = values.FirstOrDefault(sample => sample.Time >= eventTime.Value && sample.Value.HasValue &&
            (after.Value >= before.Value ? sample.Value.Value >= threshold : sample.Value.Value <= threshold));
        return reached.Value.HasValue ? reached.Time - eventTime.Value : null;
    }

    private static double? BaselineFixedFourSecond(IReadOnlyList<Sample> history)
    {
        var points = history.Where(sample => sample.RangeNm.HasValue && history[^1].Time - sample.Time <= 4).ToArray();
        if (points.Length < 3 || points[^1].Time - points[0].Time < 2 || points[^1].Time - points[^2].Time > 3) return null;
        var origin = points[0].Time;
        var weights = points.Select(sample => System.Math.Clamp(2 - ((points[^1].Time - sample.Time) / 4) * 1.5, 0.5, 2)).ToArray();
        var sumWeights = weights.Sum();
        var meanX = points.Select((sample, index) => (sample.Time - origin) / 3600 * weights[index]).Sum() / sumWeights;
        var meanRange = points.Select((sample, index) => sample.RangeNm!.Value * weights[index]).Sum() / sumWeights;
        var covariance = points.Select((sample, index) => weights[index] * ((sample.Time - origin) / 3600 - meanX) * (sample.RangeNm!.Value - meanRange)).Sum();
        var variance = points.Select((sample, index) => weights[index] * System.Math.Pow((sample.Time - origin) / 3600 - meanX, 2)).Sum();
        return variance <= 0 ? null : -covariance / variance;
    }

    private static double RecoveryTime(IReadOnlyList<Sample> samples, double eventTime, double errorLimit) =>
        samples.FirstOrDefault(sample => sample.Time > eventTime && sample.Final.HasValue &&
            System.Math.Abs(sample.Final.Value - sample.Truth) <= errorLimit)?.Time - eventTime ?? 0;

    private static double StandardDeviation(double[] values)
    {
        if (values.Length == 0) return 0;
        var mean = values.Average();
        return System.Math.Sqrt(values.Average(value => System.Math.Pow(value - mean, 2)));
    }

    private static double PeakError(IReadOnlyList<Sample> samples, Func<Sample, double?> value) =>
        samples.Where(sample => value(sample).HasValue)
            .Select(sample => System.Math.Abs(value(sample)!.Value - sample.Truth)).DefaultIfEmpty(0).Max();

    private static double MeanError(IReadOnlyList<Sample> samples, Func<Sample, double?> value)
    {
        var steady = samples.Where(sample => sample.Time >= DurationSeconds - 2 && value(sample).HasValue).ToArray();
        return steady.Length == 0 ? 0 : steady.Average(sample => System.Math.Abs(value(sample)!.Value - sample.Truth));
    }

    private static double Jitter(IReadOnlyList<Sample> samples, Func<Sample, double?> value) =>
        StandardDeviation(samples.Where(sample => sample.Time >= DurationSeconds - 2)
            .Select(value).Where(static sample => sample.HasValue).Select(static sample => sample!.Value).ToArray());

    private static string Format(double? value) => value?.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) ?? "n/a";

    private sealed record Motion(double Track, double Speed, double? ReportedTrack = null);
    private sealed record Position(double NorthNm, double EastNm);
    private sealed record Sample(
        double Time,
        double Truth,
        double? RangeNm,
        double? Position,
        double? Vector,
        double? Final,
        string Source,
        string Alignment,
        string RangeDecision);
    private sealed record Scenario(
        string Name,
        Func<double, Motion> OwnshipMotion,
        Func<double, Motion> TargetMotion,
        bool IrregularTimestamps = false,
        bool BadPositionSample = false,
        Func<double, double>? OwnshipLag = null,
        double? EventTime = null);
}
