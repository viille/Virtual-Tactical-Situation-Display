namespace TacticalDisplay.App.Cloud;

public sealed class MapFeatureSyncService
{
    private readonly VtsdCloudClient _client;
    public MapFeatureSyncService(VtsdCloudClient client) => _client = client;
    public async Task<IReadOnlyList<MapFeature>> FetchAsync(string collectionSlug, CancellationToken ct) =>
        (await _client.GetCollectionMapFeaturesAsync(collectionSlug, ct).ConfigureAwait(false)).MapFeatures;
}
