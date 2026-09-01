#!/usr/bin/env pwsh
param(
    [string]$Configuration = "Release",
    [string[]]$Runtimes = @("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"),
    [string]$OutputDir = "./dist",
    [switch]$SkipBuild,
    [switch]$NoChecksums,
    [switch]$AllowOldPowerShellVersionsAndRiskFailingScripts
)

if ($PSVersionTable.PSVersion -lt [Version]'7.6.0' -and -not $AllowOldPowerShellVersionsAndRiskFailingScripts) {
    Write-Error "This script requires PowerShell 7.6.0 or newer (pwsh). Current version: $($PSVersionTable.PSVersion). Pass -AllowOldPowerShellVersionsAndRiskFailingScripts to override."
    exit 1
}

$ErrorActionPreference = 'Stop'

$RepoRoot = $PSScriptRoot
$CliProject = "$RepoRoot/src/ZScheme.Cli/ZScheme.Cli.csproj"
$LspProject = "$RepoRoot/src/ZScheme.LanguageServer/ZScheme.LanguageServer.csproj"

# Read version from Directory.Build.props
[xml]$buildProps = Get-Content "$RepoRoot/Directory.Build.props"
$version = ($buildProps.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ }).Trim()
$gitSha = (git -C $RepoRoot rev-parse --short HEAD 2>$null)
if ($gitSha) {
    $gitSha = $gitSha.Trim()
    $fullVersion = "$version+$gitSha"
} else {
    $fullVersion = $version
}

Write-Host "Publishing ZScheme $fullVersion"
Write-Host "  Configuration: $Configuration"
Write-Host "  Runtimes: $($Runtimes -join ', ')"
Write-Host ""

# Clean/create output directory
if (Test-Path $OutputDir) {
    Remove-Item -Recurse -Force $OutputDir
}
New-Item -ItemType Directory -Path $OutputDir | Out-Null
$OutputDir = (Resolve-Path $OutputDir).Path

# Build once up front (unless skipped)
if (-not $SkipBuild) {
    Write-Host "=== Building solution ==="
    dotnet build "$RepoRoot/ZScheme.slnx" --configuration $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed" }
    Write-Host ""
}

# === Prebuilt package cache =================================================================
# Every archive carries a compiled copy of the packages so that the first compile after an
# install is instant and works offline. Building stdlib from source needs a NuGet restore and
# the .NET SDK, which is not something a freshly installed toolchain should require.
#
# ZSCHEME_CACHE_DIR points the install at a scratch directory so a developer's stale
# ~/.zscheme/cache cannot leak into the release.
#
# The scratch lives outside $OutputDir on purpose: everything in the output directory ships, and
# that has to hold by construction rather than by a cleanup succeeding. It is copied into each
# per-rid staging tree anyway, so it never needs to sit beside the archives. Wiped up front
# because the name is fixed -- a previous run's cache must not leak into this release.
Write-Host "=== Building the prebuilt package cache ==="
$pkgCacheScratch = Join-Path ([System.IO.Path]::GetTempPath()) "zscheme-pkgcache-build"
if (Test-Path $pkgCacheScratch) {
    Remove-Item -Recurse -Force $pkgCacheScratch
}
$previousCacheDir = $env:ZSCHEME_CACHE_DIR
$env:ZSCHEME_CACHE_DIR = $pkgCacheScratch
try {
    & "$RepoRoot/install-packages.ps1" -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Building the package cache failed" }
} finally {
    if ($null -eq $previousCacheDir) {
        Remove-Item Env:\ZSCHEME_CACHE_DIR -ErrorAction SilentlyContinue
    } else {
        $env:ZSCHEME_CACHE_DIR = $previousCacheDir
    }
}

$pkgCacheSource = Join-Path $pkgCacheScratch "pkg/$version"
if (-not (Test-Path $pkgCacheSource)) {
    throw "Expected a package cache at $pkgCacheSource but none was produced"
}
Write-Host "  -> $pkgCacheSource"
Write-Host ""

$artifacts = @()

foreach ($rid in $Runtimes) {
    Write-Host "=== Publishing for $rid ==="

    $stagingDir = Join-Path ([System.IO.Path]::GetTempPath()) "zscheme-publish-$rid"
    if (Test-Path $stagingDir) {
        Remove-Item -Recurse -Force $stagingDir
    }

    # Archive layout: bin/ holds the executables, with the package sources and the prebuilt
    # cache beside it. zsup installs this verbatim as ~/.zscheme/toolchains/<version>/.
    $binDir = Join-Path $stagingDir "bin"
    New-Item -ItemType Directory -Path $binDir -Force | Out-Null

    dotnet publish $CliProject `
        --configuration $Configuration `
        --runtime $rid `
        --no-self-contained `
        --nologo `
        --output $binDir
    if ($LASTEXITCODE -ne 0) { throw "CLI publish failed for $rid" }

    dotnet publish $LspProject `
        --configuration $Configuration `
        --runtime $rid `
        --no-self-contained `
        --nologo `
        --output $binDir
    if ($LASTEXITCODE -ne 0) { throw "LSP publish failed for $rid" }

    # Package sources: they make the cache self-healing if it is ever cleared, and they are what
    # lets the language server navigate into stdlib definitions. Tests are not shipped.
    $packagesDest = Join-Path $stagingDir "packages"
    New-Item -ItemType Directory -Path $packagesDest -Force | Out-Null
    Get-ChildItem "$RepoRoot/packages" -Directory |
        Where-Object { Test-Path (Join-Path $_.FullName 'package.zspkg') } |
        ForEach-Object {
            Copy-Item $_.FullName -Destination $packagesDest -Recurse -Force
            $testDir = Join-Path $packagesDest "$($_.Name)/test"
            if (Test-Path $testDir) { Remove-Item $testDir -Recurse -Force }
        }

    # Kept under a directory named for the compiler version, not flattened. The compiler reads
    # cache/pkg/<CompilerInfo.BaseVersion>/, which has nothing to do with the name the toolchain is
    # installed under -- `zsup install dev --from ...` is legal. Carrying the version here lets the
    # seeder write to the key the compiler will actually read.
    $pkgCacheDest = Join-Path $stagingDir "pkgcache/$version"
    New-Item -ItemType Directory -Path $pkgCacheDest -Force | Out-Null
    Copy-Item "$pkgCacheSource/*" -Destination $pkgCacheDest -Recurse -Force

    # Archive: .zip for Windows, .tar.gz for Linux/macOS
    $archiveName = "zscheme-$version-$rid"
    if ($rid -like "win-*") {
        $archivePath = Join-Path $OutputDir "$archiveName.zip"
        Compress-Archive -Path "$stagingDir/*" -DestinationPath $archivePath
    } else {
        $archivePath = Join-Path $OutputDir "$archiveName.tar.gz"
        # NOTE: build these on Linux. tar entries created on Windows carry mode 0644, so a
        # hand-extracted zs would not be executable. zsup forces 0755 on install, but someone
        # untarring the release by hand depends on the mode in the archive being right.
        tar -czf $archivePath -C $stagingDir .
    }

    $artifacts += $archivePath

    # Clean up staging directory
    Remove-Item -Recurse -Force $stagingDir

    Write-Host "  -> $archivePath"
    Write-Host ""
}

# Not best-effort: a cache that cannot be removed means something is still holding it, which is
# worth knowing during a release rather than never.
Remove-Item -Recurse -Force $pkgCacheScratch

# === Checksums ==============================================================================
# One aggregate file in GNU coreutils format, so `sha256sum -c SHA256SUMS` works directly and
# install.sh can grep a single line out of it.
if (-not $NoChecksums) {
    $sumsPath = Join-Path $OutputDir "SHA256SUMS"
    $lines = Get-ChildItem $OutputDir -File | Where-Object { $_.Name -ne 'SHA256SUMS' } | Sort-Object Name | ForEach-Object {
        $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $($_.Name)"
    }
    # LF explicitly rather than Set-Content's platform default. install.sh picks the line out with
    # awk and compares $2 to the asset name, and awk's default field splitting does not strip a
    # trailing \r -- so a SHA256SUMS produced by running this script on Windows makes every lookup
    # miss and the installer refuse to install an unverified asset. CI does not hit it (it passes
    # -NoChecksums and uses sha256sum), which is precisely why it would go unnoticed here.
    [System.IO.File]::WriteAllText($sumsPath, (($lines -join "`n") + "`n"))
    Write-Host "=== Checksums ==="
    Write-Host "  -> $sumsPath"
    Write-Host ""
}

Write-Host "=== Summary ==="
Write-Host "Version: $fullVersion"
Write-Host "Artifacts:"
foreach ($a in $artifacts) {
    $size = (Get-Item $a).Length
    $sizeMB = [math]::Round($size / 1MB, 2)
    Write-Host "  $a ($sizeMB MB)"
}
