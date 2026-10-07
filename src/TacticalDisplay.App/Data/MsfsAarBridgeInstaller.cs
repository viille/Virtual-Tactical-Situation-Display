using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using TacticalDisplay.App.Services;

namespace TacticalDisplay.App.Data;

public sealed record AarBridgePackageInfo(string Version, string PackageDirectory);
public enum AarBridgeInstallationState { NotInstalled, Installed, Invalid }
public sealed record AarBridgeInstallationInfo(AarBridgeInstallationState State, AarBridgePackageInfo? Package);

public sealed class Msfs2024PackagePathResolver
{
    private static readonly string[] DefaultConfigPaths =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft Flight Simulator 2024", "UserCfg.opt"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", "Microsoft.Limitless_8wekyb3d8bbwe", "LocalCache", "UserCfg.opt")
    ];

    public string? ResolveCommunity2024(string? manualCommunityFolder = null, IEnumerable<string>? configPaths = null)
    {
        if (!string.IsNullOrWhiteSpace(manualCommunityFolder))
        {
            var resolved = IsCommunity2024Folder(manualCommunityFolder) ? Path.GetFullPath(manualCommunityFolder) : null;
            DataSourceDebugLog.Info("MSFS-AAR", $"Community2024 path | source=manual path={resolved ?? "unresolved"}");
            return resolved;
        }

        foreach (var configPath in configPaths ?? DefaultConfigPaths)
        {
            if (!File.Exists(configPath)) continue;
            var text = File.ReadAllText(configPath);
            var match = Regex.Match(text, "InstalledPackagesPath\\s+\\\"(?<path>[^\\\"]+)\\\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) continue;
            var packagesRoot = Environment.ExpandEnvironmentVariables(match.Groups["path"].Value.Trim());
            foreach (var candidate in new[] { Path.Combine(packagesRoot, "Community2024"), Path.Combine(packagesRoot, "Packages", "Community2024") })
                if (IsCommunity2024Folder(candidate))
                {
                    var resolved = Path.GetFullPath(candidate);
                    DataSourceDebugLog.Info("MSFS-AAR", $"Community2024 path | source=UserCfg.opt path={resolved}");
                    return resolved;
                }
        }
        DataSourceDebugLog.Warn("MSFS-AAR", "Community2024 path could not be resolved");
        return null;
    }

    public static bool IsCommunity2024Folder(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;
        var normalized = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(Path.GetFileName(normalized), "Community2024", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class MsfsAarBridgeInstaller
{
    public const string PackageName = "vtsd-aar-bridge";
    public const string BundledArchiveResourceName = "TacticalDisplay.App.Resources.MSFS.vtsd-aar-bridge.zip";
    private const string ModuleRelativePath = "modules/vtsd_aar_bridge.wasm";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public AarBridgePackageInfo? InspectInstalled(string community2024Folder)
        => InspectInstallation(community2024Folder).Package;

    public AarBridgeInstallationState GetInstallationState(string community2024Folder)
        => InspectInstallation(community2024Folder).State;

    public AarBridgeInstallationInfo InspectInstallation(string community2024Folder)
    {
        var packagePath = GetPackagePath(community2024Folder);
        if (!Directory.Exists(packagePath) && !File.Exists(packagePath))
            return new AarBridgeInstallationInfo(AarBridgeInstallationState.NotInstalled, null);
        if (Directory.Exists(packagePath) && TryValidatePackage(packagePath, out var version))
            return new AarBridgeInstallationInfo(AarBridgeInstallationState.Installed, new AarBridgePackageInfo(version, packagePath));
        return new AarBridgeInstallationInfo(AarBridgeInstallationState.Invalid, null);
    }

    public Task<AarBridgePackageInfo> InstallOrUpdateAsync(string sourcePackageDirectory, string community2024Folder, CancellationToken cancellationToken) =>
        Task.Run(() => InstallOrUpdate(sourcePackageDirectory, community2024Folder, cancellationToken), cancellationToken);

    public async Task<AarBridgePackageInfo> InstallBundledAsync(Assembly assembly, string community2024Folder, CancellationToken cancellationToken)
    {
        await using var archiveStream = assembly.GetManifestResourceStream(BundledArchiveResourceName)
            ?? throw new FileNotFoundException("This VTSD build does not contain the compiled MSFS AAR Bridge package.");
        return await InstallArchiveAsync(archiveStream, community2024Folder, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AarBridgePackageInfo> InstallArchiveAsync(Stream archiveStream, string community2024Folder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(archiveStream);
        var extractionRoot = Path.Combine(Path.GetTempPath(), "VTSD", "AARBridge", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(extractionRoot);
        try
        {
            using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true))
            {
                var rootWithSeparator = Path.GetFullPath(extractionRoot) + Path.DirectorySeparatorChar;
                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                    var target = Path.GetFullPath(Path.Combine(extractionRoot, relative));
                    if (!target.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The bundled AAR Bridge archive contains an invalid path.");
                    if (entry.FullName.EndsWith('/'))
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    await using var input = entry.Open();
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                }
            }

            return await InstallOrUpdateAsync(extractionRoot, community2024Folder, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(extractionRoot)) Directory.Delete(extractionRoot, recursive: true);
        }
    }

    private AarBridgePackageInfo InstallOrUpdate(string sourcePackageDirectory, string community2024Folder, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(community2024Folder) || !Msfs2024PackagePathResolver.IsCommunity2024Folder(community2024Folder))
            throw new ArgumentException("Select an existing MSFS 2024 Community2024 folder.", nameof(community2024Folder));
        if (!TryValidatePackage(sourcePackageDirectory, out var sourceVersion))
            throw new InvalidDataException("The bundled AAR Bridge package is incomplete or invalid.");

        var root = Path.GetFullPath(community2024Folder);
        var destination = GetPackagePath(root);
        var staging = Path.Combine(root, $".{PackageName}.staging-{Guid.NewGuid():N}");
        var backup = Path.Combine(root, $".{PackageName}.backup-{Guid.NewGuid():N}");
        var hasPrevious = Directory.Exists(destination);
        var previousVersion = InspectInstalled(root)?.Version;
        DataSourceDebugLog.Info("MSFS-AAR", $"Bridge install | community={root} bundledVersion={sourceVersion} installedVersion={previousVersion ?? "<none>"} action={(hasPrevious ? "update" : "install")}");
        var previousMoved = false;
        var stagedPackagePromoted = false;
        try
        {
            CopyDirectory(sourcePackageDirectory, staging, cancellationToken);
            DataSourceDebugLog.Debug("MSFS-AAR", $"Bridge install stage=copy staging={staging}");
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryValidatePackage(staging, out var stagedVersion) || stagedVersion != sourceVersion)
                throw new InvalidDataException("The staged AAR Bridge package failed integrity validation.");
            DataSourceDebugLog.Debug("MSFS-AAR", $"Bridge install stage=staging-validation version={stagedVersion}");

            if (hasPrevious)
            {
                Directory.Move(destination, backup);
                previousMoved = true;
            }
            Directory.Move(staging, destination);
            stagedPackagePromoted = true;
            if (!TryValidatePackage(destination, out var installedVersion) || installedVersion != sourceVersion)
                throw new InvalidDataException("The installed AAR Bridge package failed verification.");
            DataSourceDebugLog.Info("MSFS-AAR", $"Bridge install succeeded | community={root} version={sourceVersion}");
        }
        catch (Exception installError)
        {
            try
            {
                if (Directory.Exists(destination) && stagedPackagePromoted) Directory.Delete(destination, recursive: true);
                if (previousMoved && Directory.Exists(backup) && !Directory.Exists(destination)) Directory.Move(backup, destination);
            }
            catch (Exception rollbackError)
            {
                throw new IOException($"Bridge installation failed and rollback could not restore the previous package. Its backup remains at '{backup}'.",
                    new AggregateException(installError, rollbackError));
            }
            DataSourceDebugLog.Error("MSFS-AAR", $"Bridge install failed | stage=replace oldInstallPreserved={Directory.Exists(destination)}", installError);
            throw;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }

        if (previousMoved && Directory.Exists(backup))
        {
            try { Directory.Delete(backup, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return new AarBridgePackageInfo(sourceVersion, destination);
    }

    public bool Uninstall(string community2024Folder)
    {
        if (!Directory.Exists(community2024Folder) || !Msfs2024PackagePathResolver.IsCommunity2024Folder(community2024Folder))
            throw new ArgumentException("Select an existing MSFS 2024 Community2024 folder.", nameof(community2024Folder));
        var packagePath = GetPackagePath(community2024Folder);
        if (!Directory.Exists(packagePath)) return false;
        Directory.Delete(packagePath, recursive: true);
        return true;
    }

    private static string GetPackagePath(string communityFolder) => Path.Combine(Path.GetFullPath(communityFolder), PackageName);

    private static bool TryValidatePackage(string packageDirectory, out string version)
    {
        version = string.Empty;
        if (!Directory.Exists(packageDirectory)) return false;
        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(packageDirectory, "manifest.json")));
            if (!manifest.RootElement.TryGetProperty("package_version", out var versionNode) || versionNode.ValueKind != JsonValueKind.String) return false;
            version = versionNode.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(version)) return false;
            using var layout = JsonDocument.Parse(File.ReadAllText(Path.Combine(packageDirectory, "layout.json")));
            if (!layout.RootElement.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return false;
            var packageRoot = Path.GetFullPath(packageDirectory) + Path.DirectorySeparatorChar;
            var listedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in content.EnumerateArray())
            {
                if (!entry.TryGetProperty("path", out var pathNode) || pathNode.ValueKind != JsonValueKind.String) return false;
                var relative = pathNode.GetString();
                if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) return false;
                var fullPath = Path.GetFullPath(Path.Combine(packageDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!fullPath.StartsWith(packageRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath)) return false;
                var normalizedRelative = relative.Replace('\\', '/');
                if (normalizedRelative.Equals("layout.json", StringComparison.OrdinalIgnoreCase) || !listedFiles.Add(normalizedRelative)) return false;
                var fileInfo = new FileInfo(fullPath);
                if (!entry.TryGetProperty("size", out var sizeNode) || !sizeNode.TryGetInt64(out var expectedSize) || expectedSize != fileInfo.Length) return false;
                if (!entry.TryGetProperty("hash", out var hashNode) || hashNode.ValueKind != JsonValueKind.String) return false;
                var expectedHash = hashNode.GetString();
                if (string.IsNullOrWhiteSpace(expectedHash)) return false;
                using var stream = File.OpenRead(fullPath);
                var actualHash = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(stream));
                if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) return false;
            }
            if (!listedFiles.Contains("manifest.json") || !listedFiles.Contains(ModuleRelativePath)) return false;
            var actualFiles = Directory.EnumerateFiles(packageDirectory, "*", SearchOption.AllDirectories)
                .Where(path => !Path.GetRelativePath(packageDirectory, path).Equals("layout.json", StringComparison.OrdinalIgnoreCase))
                .Select(path => Path.GetRelativePath(packageDirectory, path).Replace('\\', '/'))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return actualFiles.SetEquals(listedFiles);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (JsonException) { return false; }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
    }

    private static void CopyDirectory(string source, string destination, CancellationToken cancellationToken)
    {
        var sourceRoot = Path.GetFullPath(source);
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(sourceRoot, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, Path.GetRelativePath(sourceRoot, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }
}
