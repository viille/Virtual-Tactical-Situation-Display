param(
    [string]$SdkRoot = $env:MSFS2024_SDK
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($SdkRoot)) {
    $SdkRoot = 'C:\MSFS 2024 SDK'
}
$SdkRoot = [System.IO.Path]::GetFullPath($SdkRoot)
$requiredHeader = Join-Path $SdkRoot 'WASM\include\MSFS\MSFS.h'
if (-not (Test-Path -LiteralPath $requiredHeader -PathType Leaf)) {
    throw "MSFS 2024 SDK headers were not found at '$SdkRoot'. Set MSFS2024_SDK to the installed SDK root."
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuild = $null
if (Test-Path -LiteralPath $vswhere -PathType Leaf) {
    $msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\Current\Bin\MSBuild.exe' | Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($msbuild)) {
    $command = Get-Command 'MSBuild.exe' -ErrorAction SilentlyContinue
    if ($command) { $msbuild = $command.Source }
}
$project = Join-Path $PSScriptRoot 'MSFS.AarBridge.vcxproj'
$module = Join-Path $PSScriptRoot 'build\vtsd_aar_bridge.wasm'
if (-not [string]::IsNullOrWhiteSpace($msbuild) -and (Test-Path -LiteralPath $msbuild -PathType Leaf)) {
    & $msbuild $project '/m' '/t:Rebuild' '/p:Configuration=Release' '/p:Platform=MSFS' "/p:MSFS2024_SDK=$SdkRoot" '/verbosity:minimal'
    if ($LASTEXITCODE -ne 0) { throw "MSFS AAR Bridge MSBuild failed with exit code $LASTEXITCODE." }
}
else {
    $sdkForward = $SdkRoot.Replace('\', '/')
    $clang = Join-Path $SdkRoot 'WASM\llvm\bin\clang-cl.exe'
    $wasmLinker = Join-Path $SdkRoot 'WASM\llvm\bin\wasm-ld.exe'
    $sysroot = Join-Path $SdkRoot 'WASM\wasi-sysroot'
    $wasiLibraries = Join-Path $sysroot 'lib\wasm32-wasi'
    $versionLibrary = Join-Path $SdkRoot 'WASM\WasmVersions\MSFS_WasmVersions.a'
    $builtinsLibrary = Join-Path $wasiLibraries 'libclang_rt.builtins-wasm32.a'
    $objectDirectory = Join-Path $PSScriptRoot 'build'
    $object = Join-Path $objectDirectory 'main.o'
    $source = Join-Path $PSScriptRoot 'Source\main.cpp'
    $include = Join-Path $SdkRoot 'WASM\include'
    $includeUtils = Join-Path $include 'MSFS\Utils'
    $requiredTools = @($clang, $wasmLinker, $versionLibrary, $builtinsLibrary)
    $missingTool = $requiredTools | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -First 1
    if ($missingTool) { throw "MSFS 2024 SDK WASM compiler inputs were not found: $missingTool" }

    New-Item -ItemType Directory -Path $objectDirectory -Force | Out-Null
    $compileArguments = @(
        '-c',
        '--target=wasm32-wasi',
        "/clang:--sysroot=$sdkForward/WASM/wasi-sysroot",
        '/clang:-std=c++17',
        '/clang:-O3',
        '/EHs-c-',
        '/D__wasi__',
        '/I', $include,
        '/I', $includeUtils,
        '-o', $object,
        $source
    )
    & $clang @compileArguments
    if ($LASTEXITCODE -ne 0) { throw "MSFS AAR Bridge C++ compilation failed with exit code $LASTEXITCODE." }

    $linkArguments = @(
        '--no-entry',
        '--allow-undefined',
        '--export=module_init',
        '--export=module_deinit',
        '-L', $wasiLibraries,
        '-o', $module,
        $object,
        $versionLibrary,
        (Join-Path $wasiLibraries 'libc++.a'),
        (Join-Path $wasiLibraries 'libc++abi.a'),
        (Join-Path $wasiLibraries 'libc.a'),
        (Join-Path $wasiLibraries 'libm.a'),
        $builtinsLibrary
    )
    & $wasmLinker @linkArguments
    if ($LASTEXITCODE -ne 0) { throw "MSFS AAR Bridge WASM linking failed with exit code $LASTEXITCODE." }
}

if (-not (Test-Path -LiteralPath $module -PathType Leaf)) {
    throw "MSFS toolchain completed without producing the expected module: $module"
}

$resourcesRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\TacticalDisplay.App\Resources\MSFS'))
$package = Join-Path $resourcesRoot 'vtsd-aar-bridge'
$staging = Join-Path $resourcesRoot ".vtsd-aar-bridge.staging-$([guid]::NewGuid().ToString('N'))"
$backup = Join-Path $resourcesRoot ".vtsd-aar-bridge.backup-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path (Join-Path $staging 'modules') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Package\manifest.json') -Destination (Join-Path $staging 'manifest.json')
Copy-Item -LiteralPath $module -Destination (Join-Path $staging 'modules\vtsd_aar_bridge.wasm')

$content = @(
    Get-ChildItem -LiteralPath $staging -File -Recurse | ForEach-Object {
        $relative = [System.IO.Path]::GetRelativePath($staging, $_.FullName).Replace('\', '/')
        [ordered]@{
            path = $relative
            size = $_.Length
            date = ([DateTimeOffset]$_.LastWriteTimeUtc).ToUnixTimeSeconds()
            hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm MD5).Hash.ToLowerInvariant()
        }
    }
)
$layout = [ordered]@{ content = $content }
$layoutPath = Join-Path $staging 'layout.json'
$layout | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $layoutPath -Encoding utf8

$resolvedParent = $resourcesRoot.TrimEnd('\', '/')
foreach ($candidate in @($staging, $backup, $package)) {
    $candidatePath = [System.IO.Path]::GetFullPath($candidate)
    if ([System.IO.Path]::GetDirectoryName($candidatePath).TrimEnd('\', '/') -ne $resolvedParent) {
        throw "Refusing to replace a bridge package outside '$resolvedParent'."
    }
}

$movedPrevious = $false
try {
    if (Test-Path -LiteralPath $package) {
        Move-Item -LiteralPath $package -Destination $backup
        $movedPrevious = $true
    }
    Move-Item -LiteralPath $staging -Destination $package
    if (-not (Test-Path -LiteralPath (Join-Path $package 'modules\vtsd_aar_bridge.wasm') -PathType Leaf)) {
        throw 'The staged bridge package did not contain its WASM module after replacement.'
    }
}
catch {
    if ((Test-Path -LiteralPath $package) -and $movedPrevious -and (Test-Path -LiteralPath $backup)) {
        Remove-Item -LiteralPath $package -Recurse -Force
        Move-Item -LiteralPath $backup -Destination $package
    }
    throw
}
finally {
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
}
if ($movedPrevious) {
    try { Remove-Item -LiteralPath $backup -Recurse -Force }
    catch { Write-Warning "New bridge package is installed; backup cleanup failed at '$backup'." }
}

Write-Host "Built VTSD AAR Bridge package at $package"

$archivePath = Join-Path $resourcesRoot 'vtsd-aar-bridge.zip'
$archiveStaging = Join-Path $resourcesRoot ".vtsd-aar-bridge.staging-$([guid]::NewGuid().ToString('N')).zip"
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($package, $archiveStaging)
try {
    Move-Item -LiteralPath $archiveStaging -Destination $archivePath -Force
}
finally {
    if (Test-Path -LiteralPath $archiveStaging) { Remove-Item -LiteralPath $archiveStaging -Force }
}
Write-Host "Embedded release archive staged at $archivePath"
