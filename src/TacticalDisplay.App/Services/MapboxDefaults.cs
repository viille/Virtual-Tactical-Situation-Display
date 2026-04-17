using System.Reflection;

namespace TacticalDisplay.App.Services;

public static class MapboxDefaults
{
    public const string FallbackStyleUrl = "mapbox://styles/mapbox/outdoors-v12";
    public const string FallbackAreasStyleUrl = "mapbox://styles/ottotuhkunen/cmo1iv0wh006n01sjgbnlef0q";

    private const string AccessTokenMetadataKey = "DefaultMapboxAccessToken";
    private const string StyleUrlMetadataKey = "DefaultMapboxStyleUrl";
    private const string AreasStyleUrlMetadataKey = "DefaultMapboxAreasStyleUrl";

    public static string DefaultAccessToken { get; } = ReadAssemblyMetadata(AccessTokenMetadataKey);

    public static string DefaultStyleUrl { get; } =
        NormalizeStyleUrl(ReadAssemblyMetadata(StyleUrlMetadataKey), FallbackStyleUrl);

    public static string DefaultAreasStyleUrl { get; } =
        NormalizeStyleUrl(ReadAssemblyMetadata(AreasStyleUrlMetadataKey), FallbackAreasStyleUrl);

    public static string ResolveAccessToken() => DefaultAccessToken;

    public static string ResolveStyleUrl() => DefaultStyleUrl;

    public static string ResolveAreasStyleUrl() => DefaultAreasStyleUrl;

    public static string ResolveDisplayStyleUrl(bool useAreasStyle) =>
        useAreasStyle ? ResolveAreasStyleUrl() : ResolveStyleUrl();

    public static bool HasEmbeddedAccessToken => !string.IsNullOrWhiteSpace(DefaultAccessToken);

    private static string NormalizeStyleUrl(string? value, string fallback)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? fallback : trimmed;
    }

    private static string ReadAssemblyMetadata(string key)
    {
        var value = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, key, StringComparison.Ordinal))
            ?.Value;
        return value?.Trim() ?? string.Empty;
    }
}
