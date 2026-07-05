namespace TacticalDisplay.App.Cloud;

public sealed class KneepadSyncService
{
    private readonly VtsdCloudClient _client;
    public KneepadSyncService(VtsdCloudClient client) => _client = client;
    public async Task<IReadOnlyList<KneepadPage>> FetchAsync(string collectionSlug, CancellationToken ct) =>
        (await _client.GetCollectionPagesAsync(collectionSlug, ct).ConfigureAwait(false)).Pages;
}
