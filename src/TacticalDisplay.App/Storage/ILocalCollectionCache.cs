using TacticalDisplay.App.Cloud;

namespace TacticalDisplay.App.Storage;

public interface ILocalCollectionCache
{
    Task<IReadOnlyList<Collection>> GetCachedCollectionsAsync();
    Task SaveCollectionsAsync(IReadOnlyList<Collection> collections);
    Task<IReadOnlyList<KneepadPage>> GetCachedPagesAsync(string collectionSlug);
    Task<IReadOnlyList<MapFeature>> GetCachedMapFeaturesAsync(string collectionSlug);
    Task SaveCollectionAsync(Collection collection, IReadOnlyList<KneepadPage> pages, IReadOnlyList<MapFeature> mapFeatures);
    Task RemoveCollectionAsync(string collectionSlug);
    Task ClearAllAsync();
}
