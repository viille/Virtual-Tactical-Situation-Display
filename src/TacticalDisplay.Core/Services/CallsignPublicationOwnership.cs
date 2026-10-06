using TacticalDisplay.Core.Models;

namespace TacticalDisplay.Core.Services;

/// <summary>Maintains callsign ownership across snapshots and enforces one published owner.</summary>
public sealed class CallsignPublicationOwnership
{
    private readonly Dictionary<string, Owner> _owners = new(StringComparer.OrdinalIgnoreCase);

    public TrafficSnapshot Reconcile(
        TrafficSnapshot snapshot,
        string? ownshipCallsign,
        TimeSpan evidenceRetention)
    {
        var active = snapshot.Contacts.GroupBy(ContactKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var activeGenerationsById = snapshot.Contacts
            .GroupBy(contact => contact.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(contact => contact.Generation).ToHashSet(), StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _owners.ToArray())
        {
            var generationChanged = activeGenerationsById.TryGetValue(pair.Value.ContactId, out var activeGenerations) &&
                !activeGenerations.Contains(pair.Value.Generation);
            var directTrackDeparted = pair.Value.Source == TrackSource.TacticalLink && !activeGenerationsById.ContainsKey(pair.Value.ContactId);
            if (generationChanged || snapshot.Timestamp - pair.Value.LastSupportedAt > evidenceRetention ||
                directTrackDeparted ||
                (active.TryGetValue(pair.Value.ContactKey, out var owningContact) && owningContact.CallsignRevoked))
            {
                _owners.Remove(pair.Key);
                continue;
            }

            if (active.TryGetValue(pair.Value.ContactKey, out owningContact) &&
                !string.IsNullOrWhiteSpace(owningContact.Callsign) &&
                !string.Equals(Normalize(owningContact.Callsign), pair.Key, StringComparison.OrdinalIgnoreCase))
            {
                _owners.Remove(pair.Key);
            }
        }

        var contacts = snapshot.Contacts.ToArray();
        foreach (var group in contacts
                     .Select((contact, index) => (contact, index))
                     .Where(pair => !string.IsNullOrWhiteSpace(pair.contact.Callsign))
                     .GroupBy(pair => Normalize(pair.contact.Callsign!), StringComparer.OrdinalIgnoreCase))
        {
            var candidates = group.ToArray();
            var ownerKey = candidates.Length == 0 ? string.Empty : group.Key;
            if (!string.IsNullOrWhiteSpace(ownshipCallsign) &&
                string.Equals(ownerKey, Normalize(ownshipCallsign), StringComparison.OrdinalIgnoreCase))
            {
                foreach (var candidate in candidates)
                {
                    contacts[candidate.index] = candidate.contact with { Callsign = null, CallsignRevoked = true };
                }

                _owners.Remove(ownerKey);
                continue;
            }

            var directCandidates = candidates.Where(candidate => candidate.contact.Source == TrackSource.TacticalLink).ToArray();
            if (directCandidates.Length > 1)
            {
                foreach (var candidate in candidates)
                    contacts[candidate.index] = candidate.contact with { Callsign = null, CallsignRevoked = true };
                _owners.Remove(ownerKey);
                System.Diagnostics.Trace.TraceWarning("Multiple TacticalLink tracks claimed callsign {0}; publication suppressed.", ownerKey);
                continue;
            }

            if (directCandidates.Length == 1)
            {
                var direct = directCandidates[0];
                _owners[ownerKey] = new Owner(ContactKey(direct.contact), direct.contact.Id, direct.contact.Generation, snapshot.Timestamp, direct.contact.Source);
                foreach (var candidate in candidates)
                    if (candidate.index != direct.index)
                        contacts[candidate.index] = candidate.contact with { Callsign = null, CallsignRevoked = true };
                continue;
            }

            var selected = candidates.FirstOrDefault(candidate =>
                _owners.TryGetValue(ownerKey, out var owner) &&
                string.Equals(owner.ContactKey, ContactKey(candidate.contact), StringComparison.OrdinalIgnoreCase));
            if (selected.contact is null)
            {
                if (_owners.ContainsKey(ownerKey))
                {
                    foreach (var candidate in candidates)
                    {
                        contacts[candidate.index] = candidate.contact with { Callsign = null, CallsignRevoked = true };
                    }

                    continue;
                }

                if (candidates.Length > 1)
                {
                    foreach (var candidate in candidates)
                    {
                        contacts[candidate.index] = candidate.contact with { Callsign = null, CallsignRevoked = true };
                    }

                    continue;
                }

                selected = candidates[0];
            }

            _owners[ownerKey] = new Owner(ContactKey(selected.contact), selected.contact.Id, selected.contact.Generation, snapshot.Timestamp, selected.contact.Source);
            foreach (var candidate in candidates)
            {
                if (candidate.index != selected.index)
                {
                    contacts[candidate.index] = candidate.contact with { Callsign = null, CallsignRevoked = true };
                }
            }
        }

        return snapshot with { Contacts = contacts };
    }

    public void Clear() => _owners.Clear();

    private static string ContactKey(TrafficContactState contact) => $"{contact.Id}@{contact.Generation}";
    private static string Normalize(string callsign) => callsign.Trim().ToUpperInvariant();
    private sealed record Owner(string ContactKey, string ContactId, long Generation, DateTimeOffset LastSupportedAt, TrackSource Source);
}
