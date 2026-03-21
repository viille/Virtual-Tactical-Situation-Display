using TacticalDisplay.Core.Math;
using Xunit;

namespace TacticalDisplay.Tests;

public sealed class GeoMathTests
{
    [Fact]
    public void Distance_OneDegreeLatitude_IsAboutSixtyNm()
    {
        var nm = GeoMath.DistanceNm(60.0, 24.0, 61.0, 24.0);
        Assert.InRange(nm, 59.0, 61.0);
    }

    [Fact]
    public void Bearing_North_IsNearZero()
    {
        var brg = GeoMath.InitialBearingDeg(60.0, 24.0, 61.0, 24.0);
        Assert.True(brg >= 359.0 || brg <= 1.0);
    }

    [Fact]
    public void SignedRelativeBearing_WrapsCorrectly()
    {
        var rel = GeoMath.SignedRelativeBearingDeg(350, 10);
        Assert.InRange(rel, 19.0, 21.0);
    }
}
