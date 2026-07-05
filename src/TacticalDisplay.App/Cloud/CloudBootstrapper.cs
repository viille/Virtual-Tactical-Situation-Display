using System.IO;
using Microsoft.Extensions.DependencyInjection;
using TacticalDisplay.App.Security;
using TacticalDisplay.App.Services;
using TacticalDisplay.App.Storage;

namespace TacticalDisplay.App.Cloud;

public static class CloudBootstrapper
{
    private static readonly Lazy<ServiceProvider> Services = new(Build);
    public static IServiceProvider Provider => Services.Value;
    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddSingleton(CloudOptions.Load());
        services.AddSingleton<ISecureTokenStore, WindowsCredentialTokenStore>();
        services.AddSingleton<ILocalCollectionCache>(_ => new LocalCollectionCache(Path.Combine(AppDataPaths.CacheDirectory, "collections")));
        services.AddSingleton<CloudOverlaySettingsStore>(_ => new(Path.Combine(AppDataPaths.ApplicationDataDirectory, "cloud-overlays.json")));
        services.AddSingleton<CloudPreferencesStore>(_ => new(Path.Combine(AppDataPaths.ApplicationDataDirectory, "cloud-settings.json")));
        services.AddHttpClient<VtsdCloudClient>();
        services.AddSingleton<ICloudTelemetryService, CloudTelemetryService>();
        services.AddSingleton<DeviceLoginService>();
        services.AddSingleton<KneepadSyncService>();
        services.AddSingleton<MapFeatureSyncService>();
        services.AddSingleton<CloudContentStore>();
        services.AddSingleton<AuthService>();
        services.AddSingleton<CollectionService>();
        services.AddSingleton<CloudStartupService>();
        services.AddTransient<CloudSettingsViewModel>();
        return services.BuildServiceProvider();
    }
}
