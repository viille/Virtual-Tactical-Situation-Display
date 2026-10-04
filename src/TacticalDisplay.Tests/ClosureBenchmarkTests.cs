using TacticalDisplay.Core.Models;
using TacticalDisplay.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace TacticalDisplay.Tests;

/// <summary>Deterministic before/after benchmark: old four second filter vs repository output.</summary>
public sealed class ClosureBenchmarkTests(ITestOutputHelper output)
{
    [Fact]
    public void ClosureBenchmark_ReportsStepResponseSteadyStateAndOutlierRecovery()
    {
        const double initialKt = 100;
        const double finalKt = 500;
        const double sampleSeconds = 0.25;
        var start = DateTimeOffset.UnixEpoch;
        var repository = new TrafficRepository();
        var samples = new List<(double Time, double Range, double? NewValue)>();
        var range = 20.0;

        for (var i = 0; i <= 64; i++)
        {
            var t = i * sampleSeconds;
            var closure = t < 4 ? initialKt : finalKt;
            if (i > 0) range -= closure * sampleSeconds / 3600.0;
            var measuredRange = range + (System.Math.Abs(t - 10.25) < 0.001 ? 0.20 : 0);
            var timestamp = start.AddSeconds(t);
            var ownship = new OwnshipState("OWN", 0, 0, 5000, 90, 0, timestamp);
            var target = new TrafficContactState("T1", "BENCH1", measuredRange / 60.0, 0, 5000, 180, 250, timestamp);
            repository.ApplySnapshot(new TrafficSnapshot(ownship, [target], timestamp), new ClassificationConfig(), new TacticalDisplaySettings());
            var value = repository.BuildPicture(new TacticalDisplaySettings()).Targets.Single().ClosureKt;
            samples.Add((t, measuredRange, value));
        }

        var before = samples.Select(sample => (sample.Time, Value: BaselineFourSecondClosure(samples.Where(item => item.Time <= sample.Time).Select(item => (item.Time, item.Range)).ToArray()))).ToArray();
        var old50 = ResponseTime(before, 4, initialKt, finalKt, 0.5);
        var new50 = ResponseTime(samples.Where(sample => sample.NewValue.HasValue).Select(sample => (sample.Time, Value: sample.NewValue)).ToArray(), 4, initialKt, finalKt, 0.5);
        var old90 = ResponseTime(before, 4, initialKt, finalKt, 0.9);
        var new90 = ResponseTime(samples.Where(sample => sample.NewValue.HasValue).Select(sample => (sample.Time, Value: sample.NewValue)).ToArray(), 4, initialKt, finalKt, 0.9);
        var oldSteady = before.Where(sample => sample.Time is >= 8.5 and <= 9.75).Select(sample => sample.Value).Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        var newSteady = samples.Where(sample => sample.Time is >= 8.5 and <= 9.75).Select(sample => sample.NewValue).Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        var oldOutlier = before.Where(sample => sample.Time is >= 10 and <= 11).Select(sample => sample.Value).Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        var newOutlier = samples.Where(sample => sample.Time is >= 10 and <= 11).Select(sample => sample.NewValue).Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        var oldRecovery = RecoveryTime(before, 10.25, finalKt);
        var newRecovery = RecoveryTime(samples.Where(sample => sample.NewValue.HasValue).Select(sample => (sample.Time, Value: sample.NewValue)).ToArray(), 10.25, finalKt);

        output.WriteLine($"closure benchmark: step50 old={old50:F2}s new={new50:F2}s; step90 old={old90:F2}s new={new90:F2}s");
        output.WriteLine($"steady mean error old={MeanError(oldSteady, finalKt):F2}kt new={MeanError(newSteady, finalKt):F2}kt; jitter stddev old={StdDev(oldSteady):F2}kt new={StdDev(newSteady):F2}kt");
        output.WriteLine($"outlier max deviation old={MaxDeviation(oldOutlier, finalKt):F2}kt new={MaxDeviation(newOutlier, finalKt):F2}kt; recovery old={oldRecovery:F2}s new={newRecovery:F2}s");
        Assert.True(new50 < old50);
        Assert.True(new90 < old90);
        Assert.InRange(MeanError(newSteady, finalKt), 0, 1);
        Assert.True(StdDev(newSteady) <= StdDev(oldSteady) + 1);
        Assert.True(MaxDeviation(newOutlier, finalKt) <= MaxDeviation(oldOutlier, finalKt));
    }

    private static double? BaselineFourSecondClosure(IReadOnlyList<(double Time, double Range)> values)
    {
        var points = values.Where(point => values[^1].Time - point.Time <= 4).ToArray();
        if (points.Length < 3 || points[^1].Time - points[0].Time < 2 || points[^1].Time - points[^2].Time > 3) return null;
        var origin = points[0].Time;
        var weights = points.Select(point => System.Math.Clamp(2 - ((points[^1].Time - point.Time) / 4) * 1.5, 0.5, 2)).ToArray();
        var sumW = weights.Sum();
        var meanX = points.Select((point, index) => (point.Time - origin) / 3600 * weights[index]).Sum() / sumW;
        var meanY = points.Select((point, index) => point.Range * weights[index]).Sum() / sumW;
        var covariance = points.Select((point, index) => weights[index] * ((point.Time - origin) / 3600 - meanX) * (point.Range - meanY)).Sum();
        var variance = points.Select((point, index) => weights[index] * System.Math.Pow((point.Time - origin) / 3600 - meanX, 2)).Sum();
        return variance <= 0 ? null : -covariance / variance;
    }

    private static double ResponseTime((double Time, double? Value)[] values, double stepTime, double start, double end, double fraction)
    {
        var threshold = start + ((end - start) * fraction);
        return values.First(sample => sample.Time >= stepTime && sample.Value >= threshold).Time - stepTime;
    }

    private static double RecoveryTime((double Time, double? Value)[] values, double outlierTime, double steady) =>
        values.First(sample => sample.Time > outlierTime && sample.Value.HasValue && System.Math.Abs(sample.Value.Value - steady) < 20).Time - outlierTime;

    private static double MeanError(double[] values, double expected) => System.Math.Abs(values.Average() - expected);
    private static double StdDev(double[] values)
    {
        var mean = values.Average();
        return System.Math.Sqrt(values.Average(value => System.Math.Pow(value - mean, 2)));
    }
    private static double MaxDeviation(double[] values, double expected) => values.Max(value => System.Math.Abs(value - expected));
}
