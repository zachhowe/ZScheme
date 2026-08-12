#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Publishes the Native AOT zsup binary for one runtime identifier.
.DESCRIPTION
    Separate from publish.ps1 because AOT cannot cross-compile between operating systems: each RID
    has to be built on a matching runner, one at a time. The toolchain archives that publish.ps1
    produces are framework-dependent and are still cross-published from a single machine.
#>
param(
    [Parameter(Mandatory)]
    [ValidateSet("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")]
    [string]$Runtime,

    [string]$Configuration = "Release",
    [string]$OutputDir = "./dist",
    [switch]$AllowOldPowerShellVersionsAndRiskFailingScripts
)

if ($PSVersionTable.PSVersion -lt [Version]'7.6.0' -and -not $AllowOldPowerShellVersionsAndRiskFailingScripts) {
    Write-Error "This script requires PowerShell 7.6.0 or newer (pwsh). Current version: $($PSVersionTable.PSVersion). Pass -AllowOldPowerShellVersionsAndRiskFailingScripts to override."
    exit 1
}

$ErrorActionPreference = 'Stop'

$RepoRoot = $PSScriptRoot
$Project = "$RepoRoot/src/ZScheme.Zsup/ZScheme.Zsup.csproj"

[xml]$buildProps = Get-Content "$RepoRoot/Directory.Build.props"
$version = ($buildProps.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ }).Trim()

Write-Host "Publishing zsup $version for $Runtime"

if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir | Out-Null
}
$OutputDir = (Resolve-Path $OutputDir).Path

$stagingDir = Join-Path ([System.IO.Path]::GetTempPath()) "zsup-publish-$Runtime"
if (Test-Path $stagingDir) {
    Remove-Item -Recurse -Force $stagingDir
}

dotnet publish $Project `
    --configuration $Configuration `
    --runtime $Runtime `
    --nologo `
    --output $stagingDir
if ($LASTEXITCODE -ne 0) { throw "zsup publish failed for $Runtime" }

# Ship only the native binary. The .pdb next to it is large and of no use to end users.
$exeName = if ($Runtime -like "win-*") { "zsup.exe" } else { "zsup" }
$exePath = Join-Path $stagingDir $exeName
if (-not (Test-Path $exePath)) {
    throw "Expected $exePath but the AOT publish did not produce it"
}

$payloadDir = Join-Path $stagingDir "payload"
New-Item -ItemType Directory -Path $payloadDir -Force | Out-Null
Copy-Item $exePath -Destination $payloadDir

$archiveName = "zsup-$version-$Runtime"
if ($Runtime -like "win-*") {
    $archivePath = Join-Path $OutputDir "$archiveName.zip"
    Compress-Archive -Path "$payloadDir/*" -DestinationPath $archivePath -Force
} else {
    $archivePath = Join-Path $OutputDir "$archiveName.tar.gz"
    # Built on a matching Unix runner, so the executable bit is recorded correctly.
    tar -czf $archivePath -C $payloadDir .
}

Remove-Item -Recurse -Force $stagingDir

$sizeMB = [math]::Round((Get-Item $archivePath).Length / 1MB, 2)
Write-Host "  -> $archivePath ($sizeMB MB)"
