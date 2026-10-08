using System.IO.Compression;
using System.Text.Json;
using TacticalDisplay.App.Data;
using Xunit;

namespace TacticalDisplay.Tests;

public sealed class MsfsAarBridgeInstallerTests
{
    [Fact]
    public void ResolverUsesConfiguredInstalledPackagesPath()
    {
        using var temp = new TempDirectory();
        var root = Path.Combine(temp.Path, "MSFS");
        var community = Directory.CreateDirectory(Path.Combine(root, "Community2024")).FullName;
        var config = Path.Combine(temp.Path, "UserCfg.opt");
        File.WriteAllText(config, $"InstalledPackagesPath \"{root}\"\r\n");

        var result = new Msfs2024PackagePathResolver().ResolveCommunity2024(configPaths: [config]);

        Assert.Equal(community, result);
    }

    [Fact]
    public async Task InstallUpdateAndUninstallManageOnlyVtsdPackage()
    {
        using var temp = new TempDirectory();
        var community = Directory.CreateDirectory(Path.Combine(temp.Path, "Community2024")).FullName;
        var sibling = Directory.CreateDirectory(Path.Combine(community, "other-package")).FullName;
        File.WriteAllText(Path.Combine(sibling, "keep.txt"), "keep");
        var sourceV1 = CreatePackage(temp.Path, "source-v1", "1.0.0");
        var sourceV2 = CreatePackage(temp.Path, "source-v2", "1.1.0");
        var invalid = Directory.CreateDirectory(Path.Combine(temp.Path, "invalid-source")).FullName;
        var installer = new MsfsAarBridgeInstaller();

        var installed = await installer.InstallOrUpdateAsync(sourceV1, community, CancellationToken.None);
        var updated = await installer.InstallOrUpdateAsync(sourceV2, community, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallOrUpdateAsync(invalid, community, CancellationToken.None));

        Assert.Equal("1.0.0", installed.Version);
        Assert.Equal("1.1.0", updated.Version);
        Assert.Equal("1.1.0", installer.InspectInstalled(community)!.Version);
        Assert.True(File.Exists(Path.Combine(sibling, "keep.txt")));
        Assert.True(installer.Uninstall(community));
        Assert.False(Directory.Exists(updated.PackageDirectory));
        Assert.True(File.Exists(Path.Combine(sibling, "keep.txt")));
    }

    [Fact]
    public async Task InvalidCommunityFolderIsRejectedBeforeWriting()
    {
        using var temp = new TempDirectory();
        var notCommunity = Directory.CreateDirectory(Path.Combine(temp.Path, "Packages")).FullName;
        var source = CreatePackage(temp.Path, "source", "1.0.0");

        await Assert.ThrowsAsync<ArgumentException>(() => new MsfsAarBridgeInstaller()
            .InstallOrUpdateAsync(source, notCommunity, CancellationToken.None));
        Assert.Empty(Directory.EnumerateFileSystemEntries(notCommunity));
    }

    [Fact]
    public async Task TamperedPackageIsRejectedAndPreviousInstallIsPreserved()
    {
        using var temp = new TempDirectory();
        var community = Directory.CreateDirectory(Path.Combine(temp.Path, "Community2024")).FullName;
        var installedSource = CreatePackage(temp.Path, "installed-source", "1.0.0");
        var tamperedSource = CreatePackage(temp.Path, "tampered-source", "1.1.0");
        var installer = new MsfsAarBridgeInstaller();
        await installer.InstallOrUpdateAsync(installedSource, community, CancellationToken.None);

        File.AppendAllText(Path.Combine(tamperedSource, "modules", "vtsd_aar_bridge.wasm"), "tampered");

        await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallOrUpdateAsync(tamperedSource, community, CancellationToken.None));

        Assert.Equal("1.0.0", installer.InspectInstalled(community)!.Version);
    }

    [Fact]
    public async Task InvalidInstalledPackageIsReportedAndCanBeRepaired()
    {
        using var temp = new TempDirectory();
        var community = Directory.CreateDirectory(Path.Combine(temp.Path, "Community2024")).FullName;
        var source = CreatePackage(temp.Path, "repair-source", "1.0.0");
        var installer = new MsfsAarBridgeInstaller();
        var installed = await installer.InstallOrUpdateAsync(source, community, CancellationToken.None);
        File.AppendAllText(Path.Combine(installed.PackageDirectory, "modules", "vtsd_aar_bridge.wasm"), "tampered");

        var invalid = installer.InspectInstallation(community);
        Assert.Equal(AarBridgeInstallationState.Invalid, invalid.State);
        Assert.Null(invalid.Package);

        await installer.InstallOrUpdateAsync(source, community, CancellationToken.None);

        var repaired = installer.InspectInstallation(community);
        Assert.Equal(AarBridgeInstallationState.Installed, repaired.State);
        Assert.Equal("1.0.0", repaired.Package!.Version);
    }

    [Fact]
    public async Task PackageNameFileCollisionIsReportedAndPreserved()
    {
        using var temp = new TempDirectory();
        var community = Directory.CreateDirectory(Path.Combine(temp.Path, "Community2024")).FullName;
        var packagePath = Path.Combine(community, MsfsAarBridgeInstaller.PackageName);
        File.WriteAllText(packagePath, "unrelated collision");
        var source = CreatePackage(temp.Path, "collision-source", "1.0.0");
        var installer = new MsfsAarBridgeInstaller();

        Assert.Equal(AarBridgeInstallationState.Invalid, installer.GetInstallationState(community));
        await Assert.ThrowsAsync<IOException>(() => installer.InstallOrUpdateAsync(source, community, CancellationToken.None));

        Assert.Equal("unrelated collision", File.ReadAllText(packagePath));
    }

    [Fact]
    public async Task InstallArchiveExtractsAndValidatesBundledPackage()
    {
        using var temp = new TempDirectory();
        var community = Directory.CreateDirectory(Path.Combine(temp.Path, "Community2024")).FullName;
        var package = CreatePackage(temp.Path, "archive-source", "1.2.0");
        using var archiveStream = new MemoryStream();
        using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.EnumerateFiles(package, "*", SearchOption.AllDirectories))
                archive.CreateEntryFromFile(file, Path.GetRelativePath(package, file).Replace('\\', '/'));
        }
        archiveStream.Position = 0;

        var installed = await new MsfsAarBridgeInstaller().InstallArchiveAsync(archiveStream, community, CancellationToken.None);

        Assert.Equal("1.2.0", installed.Version);
        Assert.True(File.Exists(Path.Combine(installed.PackageDirectory, "modules", "vtsd_aar_bridge.wasm")));
    }

    [Fact]
    public async Task EmbeddedBridgeArchiveInstallsTheBuiltSdkPackage()
    {
        using var temp = new TempDirectory();
        var community = Directory.CreateDirectory(Path.Combine(temp.Path, "Community2024")).FullName;

        var installed = await new MsfsAarBridgeInstaller().InstallBundledAsync(
            typeof(MsfsAarBridgeInstaller).Assembly, community, CancellationToken.None);

        Assert.Equal("1.0.0", installed.Version);
        Assert.True(File.Exists(Path.Combine(installed.PackageDirectory, "modules", "vtsd_aar_bridge.wasm")));
    }

    [Fact]
    public async Task InstallArchiveRejectsPathTraversal()
    {
        using var temp = new TempDirectory();
        var community = Directory.CreateDirectory(Path.Combine(temp.Path, "Community2024")).FullName;
        using var archiveStream = new MemoryStream();
        using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(archive.CreateEntry("../escaped.txt").Open()))
            writer.Write("invalid");
        archiveStream.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() => new MsfsAarBridgeInstaller()
            .InstallArchiveAsync(archiveStream, community, CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(temp.Path, "escaped.txt")));
        Assert.False(Directory.Exists(Path.Combine(community, MsfsAarBridgeInstaller.PackageName)));
    }

    private static string CreatePackage(string root, string name, string version)
    {
        var package = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        Directory.CreateDirectory(Path.Combine(package, "modules"));
        File.WriteAllText(Path.Combine(package, "modules", "vtsd_aar_bridge.wasm"), "test module bytes");
        File.WriteAllText(Path.Combine(package, "manifest.json"), JsonSerializer.Serialize(new { package_version = version }));
        var files = new[] { "manifest.json", "modules/vtsd_aar_bridge.wasm" }.Select(relative =>
        {
            var path = Path.Combine(package, relative.Replace('/', Path.DirectorySeparatorChar));
            var bytes = File.ReadAllBytes(path);
            return new { path = relative, size = bytes.Length, hash = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(bytes)).ToLowerInvariant() };
        }).ToArray();
        File.WriteAllText(Path.Combine(package, "layout.json"), JsonSerializer.Serialize(new
        {
            content = files
        }));
        return package;
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("vtsd-aar-test-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
