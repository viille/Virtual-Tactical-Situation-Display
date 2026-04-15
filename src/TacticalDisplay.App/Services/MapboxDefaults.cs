using System.Reflection;

namespace TacticalDisplay.App.Services;

public static class MapboxDefaults
{
    public const string FallbackStyleUrl = "mapbox://styles/mapbox/outdoors-v12";

    private const string AccessTokenMetadataKey = "DefaultMapboxAccessToken";
    private const string StyleUrlMetadataKey = "DefaultMapboxStyleUrl";
    private const string AreasStyleUrlMetadataKey = "DefaultMapboxAreasStyleUrl";

    public static string DefaultAccessToken { get; } = ReadAssemblyMetadata(AccessTokenMetadataKey);

    public static string DefaultStyleUrl { get; } =
        NormalizeStyleUrl(ReadAssemblyMetadata(StyleUrlMetadataKey), FallbackStyleUrl);

    public static string DefaultAreasStyleUrl { get; } = ReadAssemblyMetadata(AreasStyleUrlMetadataKey);

    public static string ResolveAccessToken() => DefaultAccessToken;

    public static string ResolveStyleUrl() => DefaultStyleUrl;

    public static string ResolveAreasStyleUrl() => DefaultAreasStyleUrl;

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
