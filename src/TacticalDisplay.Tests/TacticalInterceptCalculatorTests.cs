using TacticalDisplay.Core.Models;
using TacticalDisplay.Core.Services;
using Xunit;

namespace TacticalDisplay.Tests;

public sealed class TacticalInterceptCalculatorTests
{
    [Fact]
    public void Calculate_ReturnsInterceptForHeadOnTarget()
    {
        var now = DateTimeOffset.UtcNow;
        var ownship = new OwnshipState("OWN", 60.0, 24.0, 5000, 0, 300, now);
        var target = new ComputedTarget(
            "T1",
            "T1",
            TargetCategory.Unknown,
            false,
            30,
            0,
            0,
            0,
            180,
            300,
            null,
            [],
            now);

        var solution = TacticalInterceptCalculator.Calculate(ownship, target);

        Assert.True(solution.HasSolution);
        Assert.InRange(solution.HeadingDeg, 0, 0.1);
        Assert.InRange(solution.TimeSeconds, 179.9, 180.1);
        Assert.InRange(solution.LatitudeDeg, 60.24, 60.26);
    }

    [Fact]
    public void Calculate_ReturnsNoSolutionWhenOwnshipHasNoUsefulSpeed()
    {
        var now = DateTimeOffset.UtcNow;
        var ownship = new OwnshipState("OWN", 60.0, 24.0, 5000, 0, 0, now);
        var target = new ComputedTarget(
            "T1",
            "T1",
            TargetCategory.Unknown,
            false,
            30,
            0,
            0,
            0,
            180,
            300,
            null,
            [],
            now);

        var solution = TacticalInterceptCalculator.Calculate(ownship, target);

        Assert.False(solution.HasSolution);
    }
}
