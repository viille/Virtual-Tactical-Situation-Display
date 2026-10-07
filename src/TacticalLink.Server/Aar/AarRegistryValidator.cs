namespace TacticalLink.Server.Aar;

public static class AarRegistryValidator
{
    private static readonly HashSet<string> TankerMethods = ["Boom", "CenterlineDrogue", "WingDrogue", "BoomDrogueAdapter"];
    private static readonly HashSet<string> ReceiverMethods = ["BoomReceptacle", "Probe"];
    private static readonly HashSet<string> Provenance = ["confirmed_aircraft_specific_value", "method_fallback", "unknown"];
    private static readonly HashSet<string> SharedDesignators = ["B762", "DC10", "A332"];

    public static bool TryValidate(AarRegistrySnapshot? snapshot, out string reason)
    {
        if (snapshot is null) return Fail("Snapshot is missing.", out reason);
        if (snapshot.SchemaVersion != 1 || snapshot.Version < 1 || snapshot.PublishedAt == default) return Fail("Registry version or schema is unsupported.", out reason);
        if (snapshot.Profiles is null || snapshot.Profiles.Count > 1000) return Fail("Profile collection is missing or too large.", out reason);

        var profileKeys = new HashSet<string>(StringComparer.Ordinal);
        var activeDesignators = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in snapshot.Profiles)
        {
            if (profile is null || string.IsNullOrWhiteSpace(profile.ProfileKey) || profile.ProfileKey.Length > 80 ||
                string.IsNullOrWhiteSpace(profile.DisplayName) || profile.DisplayName.Length > 160 || !profileKeys.Add(profile.ProfileKey))
                return Fail("Profile identity is missing, oversized, or duplicated.", out reason);
            if (profile.IcaoDesignators is null || profile.IcaoDesignators.Count == 0 || profile.IcaoDesignators.Count > 24 ||
                profile.CanTanker && (profile.TankerSystems is null || profile.TankerSystems.Count == 0) ||
                profile.CanReceive && (profile.ReceiverSystems is null || profile.ReceiverSystems.Count == 0) ||
                !profile.CanTanker && !profile.CanReceive)
                return Fail($"Profile {profile.ProfileKey} has an invalid capability/system definition.", out reason);

            var sourceIds = new HashSet<string>(profile.Sources?.Select(source => source.Id) ?? [], StringComparer.Ordinal);
            foreach (var code in profile.IcaoDesignators)
            {
                if (string.IsNullOrWhiteSpace(code) || code.Length is < 2 or > 4 || code.Any(character => !char.IsAsciiLetterOrDigit(character)) || code != code.ToUpperInvariant())
                    return Fail($"Profile {profile.ProfileKey} has an invalid ICAO designator.", out reason);
                if (profile.Enabled && !activeDesignators.Add(code)) return Fail($"Active designator {code} is ambiguous.", out reason);
                if (SharedDesignators.Contains(code) && string.IsNullOrWhiteSpace(profile.Uncertainty) && string.IsNullOrWhiteSpace(profile.Notes))
                    return Fail($"Profile {profile.ProfileKey} lacks the shared-designator caveat for {code}.", out reason);
            }
            if (!ValidateSystems(profile.TankerSystems, TankerMethods, sourceIds, system => system.Method, system => system.MaxOffloadKgPerSecond, system => system.ValueProvenance, system => system.SourceIds) ||
                !ValidateSystems(profile.ReceiverSystems, ReceiverMethods, sourceIds, system => system.Method, system => system.MaxReceiveKgPerSecond, system => system.ValueProvenance, system => system.SourceIds))
                return Fail($"Profile {profile.ProfileKey} has an invalid refueling system or rate provenance.", out reason);
        }
        reason = string.Empty;
        return true;
    }

    private static bool ValidateSystems<T>(IReadOnlyList<T>? systems, HashSet<string> allowed, HashSet<string> sourceIds,
        Func<T, string> method, Func<T, double?> value, Func<T, string> provenance, Func<T, IReadOnlyList<string>> citations)
    {
        if (systems is null) return false;
        var methods = new HashSet<string>(StringComparer.Ordinal);
        foreach (var system in systems)
        {
            var systemMethod = method(system);
            var systemProvenance = provenance(system);
            var systemValue = value(system);
            var sourceRefs = citations(system);
            if (!allowed.Contains(systemMethod) || !methods.Add(systemMethod) || !Provenance.Contains(systemProvenance) || sourceRefs is null) return false;
            if (systemProvenance == "confirmed_aircraft_specific_value")
            {
                if (systemValue is not > 0 || !double.IsFinite(systemValue.Value) || !sourceRefs.Any(sourceIds.Contains)) return false;
            }
            else if (systemValue is not null) return false;
            if (sourceRefs.Any(sourceId => !sourceIds.Contains(sourceId))) return false;
        }
        return true;
    }

    private static bool Fail(string message, out string reason) { reason = message; return false; }
}
