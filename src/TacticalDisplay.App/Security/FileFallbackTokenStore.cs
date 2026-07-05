using System.IO;

namespace TacticalDisplay.App.Security;

/// <summary>
/// Technical-debt fallback for environments where native secure storage is unavailable.
/// It is deliberately opt-in and is never registered by the production client.
/// </summary>
public sealed class FileFallbackTokenStore : ISecureTokenStore
{
    private readonly string _path;
    public FileFallbackTokenStore(string path, bool explicitlyAllowInsecureStorage)
    {
        if (!explicitlyAllowInsecureStorage)
            throw new InvalidOperationException("Plaintext token fallback requires explicit acceptance and must not be enabled in production.");
        _path = path;
    }
    public async Task<string?> GetSessionTokenAsync() => File.Exists(_path) ? await File.ReadAllTextAsync(_path).ConfigureAwait(false) : null;
    public async Task SetSessionTokenAsync(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token); Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await File.WriteAllTextAsync(_path, token).ConfigureAwait(false);
    }
    public Task ClearSessionTokenAsync() { if (File.Exists(_path)) File.Delete(_path); return Task.CompletedTask; }
}
