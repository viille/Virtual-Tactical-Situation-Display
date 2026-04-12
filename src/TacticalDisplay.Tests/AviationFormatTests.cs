using TacticalDisplay.Core.Formatting;
using Xunit;

namespace TacticalDisplay.Tests;

public sealed class AviationFormatTests
{
    [Fact]
    public void TargetAspect_UsesOtherAircraftPerspectiveForHotAndCold()
    {
        // Target heading 270: ownship east of the target is behind it, ownship west of it is ahead.
        Assert.Equal("HOT", AviationFormat.TargetAspect(270, 90));
        Assert.Equal("COLD", AviationFormat.TargetAspect(270, 270));
    }

    [Fact]
    public void TargetAspect_UsesOtherAircraftPerspectiveForIntermediateSectors()
    {
        Assert.Equal("FLANK", AviationFormat.TargetAspect(270, 30));
        Assert.Equal("BEAM", AviationFormat.TargetAspect(270, 0));
        Assert.Equal("DRAG", AviationFormat.TargetAspect(270, 310));
    }
}
