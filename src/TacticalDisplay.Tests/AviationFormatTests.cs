using TacticalDisplay.Core.Formatting;
using Xunit;

namespace TacticalDisplay.Tests;

public sealed class AviationFormatTests
{
    [Fact]
    public void TargetAspect_UsesHotAndCold()
    {
        Assert.Equal("HOT", AviationFormat.TargetAspect(270, 90));
        Assert.Equal("COLD", AviationFormat.TargetAspect(90, 90));
    }

    [Fact]
    public void TargetAspect_UsesIntermediateSectors()
    {
        Assert.Equal("FLANK", AviationFormat.TargetAspect(210, 90));
        Assert.Equal("BEAM", AviationFormat.TargetAspect(170, 90));
        Assert.Equal("DRAG", AviationFormat.TargetAspect(130, 90));
    }
}
