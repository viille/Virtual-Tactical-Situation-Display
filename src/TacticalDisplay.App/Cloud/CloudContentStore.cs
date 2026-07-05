using TacticalDisplay.App.Storage;

namespace TacticalDisplay.App.Cloud;

public sealed class CloudContentStore
{
    private readonly ILocalCollectionCache _cache; private readonly object _gate = new(); private bool _cacheLoaded; private bool _onlineReconciled;
    public CloudContentStore(ILocalCollectionCache cache) => _cache = cache;
    public IReadOnlyList<Collection> Collections { get; private set; } = [];
    public IReadOnlyDictionary<string, IReadOnlyList<KneepadPage>> Pages { get; private set; } = new Dictionary<string, IReadOnlyList<KneepadPage>>();
    public IReadOnlyDictionary<string, IReadOnlyList<MapFeature>> MapFeatures { get; private set; } = new Dictionary<string, IReadOnlyList<MapFeature>>();
    public bool IsInitialized { get { lock (_gate) return _cacheLoaded || _onlineReconciled; } }

    public async Task LoadAuthorizedCacheAsync()
    {
        var cachedCollections = await _cache.GetCachedCollectionsAsync().ConfigureAwait(false);
        var cachedPages = new Dictionary<string, IReadOnlyList<KneepadPage>>(StringComparer.OrdinalIgnoreCase);
        var cachedFeatures = new Dictionary<string, IReadOnlyList<MapFeature>>(StringComparer.OrdinalIgnoreCase);
        foreach (var collection in cachedCollections)
        {
            cachedPages[collection.Slug] = await _cache.GetCachedPagesAsync(collection.Slug).ConfigureAwait(false);
            cachedFeatures[collection.Slug] = await _cache.GetCachedMapFeaturesAsync(collection.Slug).ConfigureAwait(false);
        }
        lock (_gate)
        {
            if (_onlineReconciled) return;
            var collections = Collections.ToDictionary(x => x.Slug, StringComparer.OrdinalIgnoreCase);
            foreach (var collection in cachedCollections) collections[collection.Slug] = collection;
            foreach (var item in Pages.Where(x => !cachedPages.ContainsKey(x.Key))) cachedPages[item.Key] = item.Value;
            foreach (var item in MapFeatures.Where(x => !cachedFeatures.ContainsKey(x.Key))) cachedFeatures[item.Key] = item.Value;
            Collections = collections.Values.ToList(); Pages = cachedPages; MapFeatures = cachedFeatures; _cacheLoaded = true;
        }
    }

    public async Task StoreAuthorizedContentAsync(Collection collection, IReadOnlyList<KneepadPage> pages,
        IReadOnlyList<MapFeature> mapFeatures, bool cacheOffline)
    {
        if (cacheOffline) await _cache.SaveCollectionAsync(collection, pages, mapFeatures).ConfigureAwait(false);
        else await _cache.RemoveCollectionAsync(collection.Slug).ConfigureAwait(false);
        lock (_gate)
        {
            var collections = Collections.Where(x => !string.Equals(x.Slug, collection.Slug, StringComparison.OrdinalIgnoreCase)).Append(collection).ToList();
            var pageMap = new Dictionary<string, IReadOnlyList<KneepadPage>>(Pages, StringComparer.OrdinalIgnoreCase) { [collection.Slug] = pages };
            var featureMap = new Dictionary<string, IReadOnlyList<MapFeature>>(MapFeatures, StringComparer.OrdinalIgnoreCase) { [collection.Slug] = mapFeatures };
            Collections = collections; Pages = pageMap; MapFeatures = featureMap;
        }
    }

    public Task RemoveOfflineCacheAsync(string collectionSlug) => _cache.RemoveCollectionAsync(collectionSlug);
    public void ReconcileOnlineAuthorization(IReadOnlyList<Collection> authorizedCollections)
    {
        var allowed = authorizedCollections.Select(x => x.Slug).ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            _onlineReconciled = true;
            Collections = authorizedCollections.ToList();
            Pages = Pages.Where(x => allowed.Contains(x.Key)).ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
            MapFeatures = MapFeatures.Where(x => allowed.Contains(x.Key)).ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        }
    }
    public IReadOnlyList<KneepadPage> GetPages(string collectionSlug) => Pages.TryGetValue(collectionSlug, out var pages) ? pages : [];
    public IReadOnlyList<MapFeature> GetMapFeatures(string collectionSlug) => MapFeatures.TryGetValue(collectionSlug, out var features) ? features : [];
    public bool HasContent(string collectionSlug) => Pages.ContainsKey(collectionSlug) || MapFeatures.ContainsKey(collectionSlug);
}
