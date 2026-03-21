using TacticalDisplay.Core.Math;
using TacticalDisplay.Core.Formatting;

var failures = new List<string>();
Test("Distance 1 deg latitude ~60 NM", () =>
{
    var nm = GeoMath.DistanceNm(60.0, 24.0, 61.0, 24.0);
    return nm is > 59 and < 61;
});

Test("Bearing north is ~0", () =>
{
    var brg = GeoMath.InitialBearingDeg(60.0, 24.0, 61.0, 24.0);
    return brg is >= 359 or <= 1;
});

Test("Relative bearing signed", () =>
{
    var rel = GeoMath.SignedRelativeBearingDeg(350, 10);
    return rel is > 19 and < 21;
});

Test("Target aspect format uses hot and cold", () =>
{
    return AviationFormat.TargetAspect(270, 90) == "HOT" &&
           AviationFormat.TargetAspect(90, 90) == "COLD";
});

Test("Target aspect format uses intermediate sectors", () =>
{
    return AviationFormat.TargetAspect(210, 90) == "FLANK" &&
           AviationFormat.TargetAspect(170, 90) == "BEAM" &&
           AviationFormat.TargetAspect(130, 90) == "DRAG";
});

if (failures.Count == 0)
{
    Console.WriteLine("All tactical math tests passed.");
    return;
}

Console.WriteLine("Failed tests:");
foreach (var failure in failures)
{
    Console.WriteLine($"- {failure}");
}

Environment.ExitCode = 1;
return;

void Test(string name, Func<bool> test)
{
    if (!test())
    {
        failures.Add(name);
    }
}
