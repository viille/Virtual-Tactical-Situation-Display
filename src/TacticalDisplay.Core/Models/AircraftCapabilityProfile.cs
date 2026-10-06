namespace TacticalDisplay.Core.Models;

public sealed record AircraftCapabilityProfile(bool CanTanker, bool CanReceive);

public interface IAircraftCapabilityResolver
{
    AircraftCapabilityProfile Resolve(string? aircraftType);
}

public sealed class StaticAircraftCapabilityResolver : IAircraftCapabilityResolver
{
    private static readonly HashSet<string> Tankers = ["KC135", "KC10", "KC46", "A330MRTT", "IL78"];
    private static readonly HashSet<string> Receivers = ["F16C", "F16D", "F18C", "F18E", "F18F", "F35A", "F35B", "F35C", "F22A", "A10A", "A10C", "MIR2000", "EF2000", "GRIPEN", "C130J", "C17", "A400M"];

    public AircraftCapabilityProfile Resolve(string? aircraftType)
    {
        var normalized = new string((aircraftType ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        return new AircraftCapabilityProfile(Tankers.Contains(normalized), Receivers.Contains(normalized));
    }
}
