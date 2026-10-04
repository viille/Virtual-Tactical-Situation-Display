using TacticalDisplay.Core.Models;
using TacticalDisplay.Core.Services;
using Xunit;

namespace TacticalDisplay.Tests;

public sealed class CallsignPublicationOwnershipTests
{
    [Fact]
    public void Reconcile_DuplicateCallsignHasOnlyOnePublishedOwnerAcrossSnapshots()
    {
        var ownership = new CallsignPublicationOwnership();
        var now = DateTimeOffset.UtcNow;
        var first = Snapshot(now, Contact("A", "RETRO61"));
        var published = ownership.Reconcile(first, null, TimeSpan.FromSeconds(15));
        Assert.Equal("RETRO61", published.Contacts[0].Callsign);

        var reversed = Snapshot(now.AddSeconds(1), Contact("B", "RETRO61"), Contact("A", "RETRO61"));
        published = ownership.Reconcile(reversed, null, TimeSpan.FromSeconds(15));
        Assert.Null(published.Contacts[0].Callsign);
        Assert.Equal("RETRO61", published.Contacts[1].Callsign);
    }

    [Fact]
    public void Reconcile_RevocationReleasesOwnerForAtomicHandoff()
    {
        var ownership = new CallsignPublicationOwnership();
        var now = DateTimeOffset.UtcNow;
        _ = ownership.Reconcile(Snapshot(now, Contact("A", "FIN123")), null, TimeSpan.FromSeconds(15));
        var handoff = Snapshot(now.AddSeconds(1), Contact("A", null, revoked: true), Contact("B", "FIN123"));
        var published = ownership.Reconcile(handoff, null, TimeSpan.FromSeconds(15));
        Assert.Null(published.Contacts[0].Callsign);
        Assert.Equal("FIN123", published.Contacts[1].Callsign);
    }

    [Fact]
    public void Reconcile_ExpiredOwnerCanTransferAndNewGenerationIsDifferentIdentity()
    {
        var ownership = new CallsignPublicationOwnership();
        var now = DateTimeOffset.UtcNow;
        _ = ownership.Reconcile(Snapshot(now, Contact("A", "FIN123", generation: 4)), null, TimeSpan.FromSeconds(15));
        var later = Snapshot(now.AddSeconds(16), Contact("A", null, generation: 5), Contact("B", "FIN123"));
        var published = ownership.Reconcile(later, null, TimeSpan.FromSeconds(15));
        Assert.Equal("FIN123", published.Contacts[1].Callsign);
    }

    [Fact]
    public void Reconcile_ExcludesOwnshipCallsignAtPublicationBoundary()
    {
        var ownership = new CallsignPublicationOwnership();
        var published = ownership.Reconcile(Snapshot(DateTimeOffset.UtcNow, Contact("A", "OWN1")), "OWN1", TimeSpan.FromSeconds(15));
        Assert.Null(published.Contacts.Single().Callsign);
        Assert.True(published.Contacts.Single().CallsignRevoked);
    }

    private static TrafficContactState Contact(string id, string? callsign, bool revoked = false, long generation = 0) =>
        new(id, callsign, 60, 24, 5000, 90, 250, DateTimeOffset.UtcNow, revoked, generation);

    private static TrafficSnapshot Snapshot(DateTimeOffset timestamp, params TrafficContactState[] contacts) =>
        new(new OwnshipState("OWN", 60, 24, 5000, 0, 250, timestamp),
            contacts.Select(contact => contact with { Timestamp = timestamp }).ToArray(), timestamp);
}
