using System.IO;
using System.Text.Json;
using TacticalDisplay.App.Cloud;

namespace TacticalDisplay.App.Storage;

public sealed class LocalCollectionCache : ILocalCollectionCache
{
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public LocalCollectionCache(string root) => _root = Path.GetFullPath(root);

    public async Task<IReadOnlyList<Collection>> GetCachedCollectionsAsync() =>
        await ReadAsync<List<Collection>>(Path.Combine(_root, "collections.json")).ConfigureAwait(false) ?? [];

    public async Task SaveCollectionsAsync(IReadOnlyList<Collection> collections)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var existing = (await ReadAsync<List<Collection>>(Path.Combine(_root, "collections.json")).ConfigureAwait(false) ?? [])
                .ToDictionary(item => item.Slug, StringComparer.OrdinalIgnoreCase);
            var merged = new List<Collection>();
            foreach (var collection in collections.Where(static item => !string.IsNullOrWhiteSpace(item.Slug)))
            {
                ValidateSlug(collection.Slug);
                if (existing.TryGetValue(collection.Slug, out var cached))
                {
                    collection.CachedVersion = cached.CachedVersion;
                    collection.LastSyncedAt = cached.LastSyncedAt;
                }

                merged.Add(collection);
            }

            await WriteAsync(Path.Combine(_root, "collections.json"), merged).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<KneepadPage>> GetCachedPagesAsync(string slug) =>
        await ReadAsync<List<KneepadPage>>(Path.Combine(CollectionPath(slug), "pages.json")).ConfigureAwait(false) ?? [];
    public async Task<IReadOnlyList<MapFeature>> GetCachedMapFeaturesAsync(string slug) =>
        await ReadAsync<List<MapFeature>>(Path.Combine(CollectionPath(slug), "map-features.json")).ConfigureAwait(false) ?? [];

    public async Task SaveCollectionAsync(Collection collection, IReadOnlyList<KneepadPage> pages, IReadOnlyList<MapFeature> mapFeatures)
    {
        ValidateSlug(collection.Slug);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var existing = (await ReadAsync<List<Collection>>(Path.Combine(_root, "collections.json")).ConfigureAwait(false) ?? [])
                .Where(item => !string.Equals(item.Slug, collection.Slug, StringComparison.OrdinalIgnoreCase)).ToList();
            collection.LastSyncedAt = DateTimeOffset.UtcNow; collection.CachedVersion = collection.CurrentVersion; existing.Add(collection);
            await WriteAsync(Path.Combine(_root, "collections.json"), existing).ConfigureAwait(false);
            var directory = CollectionPath(collection.Slug); Directory.CreateDirectory(directory);
            await WriteAsync(Path.Combine(directory, "metadata.json"), collection).ConfigureAwait(false);
            await WriteAsync(Path.Combine(directory, "pages.json"), pages).ConfigureAwait(false);
            await WriteAsync(Path.Combine(directory, "map-features.json"), mapFeatures).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveCollectionAsync(string slug)
    {
        ValidateSlug(slug);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var collections = (await ReadAsync<List<Collection>>(Path.Combine(_root, "collections.json")).ConfigureAwait(false) ?? [])
                .Where(item => !string.Equals(item.Slug, slug, StringComparison.OrdinalIgnoreCase)).ToList();
            await WriteAsync(Path.Combine(_root, "collections.json"), collections).ConfigureAwait(false);
            var path = CollectionPath(slug); if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        finally { _gate.Release(); }
    }

    public async Task ClearAllAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        finally { _gate.Release(); }
    }

    private string CollectionPath(string slug) { ValidateSlug(slug); return Path.Combine(_root, slug); }
    private static void ValidateSlug(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug) || slug is "." or ".." || slug.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')))
            throw new ArgumentException("Invalid collection slug.", nameof(slug));
    }
    private static async Task<T?> ReadAsync<T>(string path)
    {
        try
        {
            if (!File.Exists(path)) return default;
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions.Options).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return default; }
    }
    private static async Task WriteAsync<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await using (var stream = File.Create(temp)) await JsonSerializer.SerializeAsync(stream, value, JsonOptions.Options).ConfigureAwait(false);
        File.Move(temp, path, true);
    }
}
