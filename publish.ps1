#!/usr/bin/env pwsh
param(
    [string]$Configuration = "Release",
    [string[]]$Runtimes = @("win-x64", "linux-x64", "osx-x64", "osx-arm64"),
    [string]$OutputDir = "./dist",
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

$RepoRoot = $PSScriptRoot
$CliProject = "$RepoRoot/src/ZScheme.Cli/ZScheme.Cli.csproj"

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

# Build once up front (unless skipped)
if (-not $SkipBuild) {
    Write-Host "=== Building solution ==="
    dotnet build "$RepoRoot/ZScheme.slnx" --configuration $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed" }
    Write-Host ""
}

$artifacts = @()

foreach ($rid in $Runtimes) {
    Write-Host "=== Publishing for $rid ==="

    $stagingDir = Join-Path ([System.IO.Path]::GetTempPath()) "zscheme-publish-$rid"
    if (Test-Path $stagingDir) {
        Remove-Item -Recurse -Force $stagingDir
    }

    dotnet publish $CliProject `
        --configuration $Configuration `
        --runtime $rid `
        --no-self-contained `
        --nologo `
        --output $stagingDir
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $rid" }

    # Archive: .zip for Windows, .tar.gz for Linux/macOS
    $archiveName = "zscheme-$version-$rid"
    if ($rid -like "win-*") {
        $archivePath = Join-Path $OutputDir "$archiveName.zip"
        Compress-Archive -Path "$stagingDir/*" -DestinationPath $archivePath
    } else {
        $archivePath = Join-Path $OutputDir "$archiveName.tar.gz"
        tar -czf $archivePath -C $stagingDir .
    }

    $artifacts += $archivePath

    # Clean up staging directory
    Remove-Item -Recurse -Force $stagingDir

    Write-Host "  -> $archivePath"
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
