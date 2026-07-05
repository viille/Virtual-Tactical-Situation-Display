using TacticalDisplay.App.Storage;

namespace TacticalDisplay.App.Cloud;

public sealed class CollectionService
{
    private readonly VtsdCloudClient _client; private readonly KneepadSyncService _kneepads; private readonly MapFeatureSyncService _mapFeatures;
    private readonly ILocalCollectionCache _cache; private readonly CloudContentStore _content; private readonly ICloudTelemetryService _telemetry;
    public CollectionService(VtsdCloudClient client, KneepadSyncService kneepads, MapFeatureSyncService mapFeatures,
        ILocalCollectionCache cache, CloudContentStore content, ICloudTelemetryService telemetry)
    { _client = client; _kneepads = kneepads; _mapFeatures = mapFeatures; _cache = cache; _content = content; _telemetry = telemetry; }
    public Task<IReadOnlyList<Collection>> LoadCachedAsync() => _cache.GetCachedCollectionsAsync();
    public async Task<IReadOnlyList<Collection>> FetchAsync(CancellationToken ct)
    {
        try
        {
            var result = (await _client.ListCollectionsAsync(ct).ConfigureAwait(false)).Collections;
            await _cache.SaveCollectionsAsync(result).ConfigureAwait(false);
            await _telemetry.TrackAsync("collections_fetched", new Dictionary<string, object?> { ["count"] = result.Count }, ct).ConfigureAwait(false);
            return result;
        }
        catch (CloudApiException ex) { await _telemetry.TrackApiErrorAsync(ex).ConfigureAwait(false); throw; }
    }
    public async Task SyncAsync(Collection collection, CancellationToken ct)
    {
        try
        {
            var pages = await _kneepads.FetchAsync(collection.Slug, ct).ConfigureAwait(false);
            var features = await _mapFeatures.FetchAsync(collection.Slug, ct).ConfigureAwait(false);
            await _content.StoreAuthorizedContentAsync(collection, pages, features, collection.CacheOffline).ConfigureAwait(false);
            await _telemetry.TrackAsync("collection_synced", ct: ct).ConfigureAwait(false);
        }
        catch (CloudApiException ex)
        {
            await _telemetry.TrackApiErrorAsync(ex).ConfigureAwait(false);
            await _telemetry.TrackAsync("sync_failed", ct: CancellationToken.None).ConfigureAwait(false); throw;
        }
    }
    public Task RemoveCachedAsync(string collectionSlug) => _content.RemoveOfflineCacheAsync(collectionSlug);
    public async Task<RedeemShareCodeResponse> RedeemAsync(string code, CancellationToken ct)
    {
        try
        {
            var response = await _client.RedeemShareCodeAsync(code, ct).ConfigureAwait(false);
            await _telemetry.TrackAsync("share_code_redeemed", ct: ct).ConfigureAwait(false);
            return response;
        }
        catch (CloudApiException ex) { await _telemetry.TrackApiErrorAsync(ex).ConfigureAwait(false); throw; }
    }
}
