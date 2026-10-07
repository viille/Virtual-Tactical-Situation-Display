using TacticalDisplay.Core.Models;
using System.Text.Json;

namespace TacticalLink.Server.Aar;

public sealed record AarRegistrySnapshot(
    int SchemaVersion,
    int Version,
    DateTimeOffset PublishedAt,
    IReadOnlyList<AarAircraftProfile> Profiles);

public sealed record AarAircraftProfile(
    string ProfileKey,
    string DisplayName,
    IReadOnlyList<string> IcaoDesignators,
    bool Enabled,
    bool CanTanker,
    bool CanReceive,
    IReadOnlyList<AarTankerSystem> TankerSystems,
    IReadOnlyList<AarReceiverSystem> ReceiverSystems,
    IReadOnlyList<AarRegistrySource> Sources,
    string? Uncertainty,
    string? Notes,
    JsonElement? Geometry = null);

public sealed record AarTankerSystem(string Method, double? MaxOffloadKgPerSecond, string ValueProvenance, IReadOnlyList<string> SourceIds);
public sealed record AarReceiverSystem(string Method, double? MaxReceiveKgPerSecond, string ValueProvenance, IReadOnlyList<string> SourceIds);
public sealed record AarRegistrySource(string Id, string Title, string Publisher, string Url, string Claim, DateTimeOffset RetrievedAt);

public interface IAarRegistryProvider
{
    AarRegistrySnapshot? Current { get; }
    bool IsAvailable { get; }
    string? Status { get; }
    event EventHandler? Changed;
}

public sealed class AarAircraftCapabilityResolver(IAarRegistryProvider registry) : IAircraftCapabilityResolver
{
    public AircraftCapabilityProfile Resolve(string? aircraftType)
    {
        var normalized = new string((aircraftType ?? string.Empty).Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        var profile = registry.Current?.Profiles.FirstOrDefault(candidate => candidate.Enabled && candidate.IcaoDesignators.Contains(normalized, StringComparer.Ordinal));
        return profile is null
            ? new AircraftCapabilityProfile(false, false)
            : new AircraftCapabilityProfile(profile.CanTanker, profile.CanReceive);
    }
}
