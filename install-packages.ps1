#!/usr/bin/env pwsh
param(
    [switch]$Debug,
    [switch]$AllowOldPowerShellVersionsAndRiskFailingScripts
)

if ($PSVersionTable.PSVersion -lt [Version]'7.6.0' -and -not $AllowOldPowerShellVersionsAndRiskFailingScripts) {
    Write-Error "This script requires PowerShell 7.6.0 or newer (pwsh). Current version: $($PSVersionTable.PSVersion). Pass -AllowOldPowerShellVersionsAndRiskFailingScripts to override."
    exit 1
}

$ErrorActionPreference = 'Stop'

$RepoRoot = $PSScriptRoot
$DebugArgs = if ($Debug) { @('--debug') } else { @() }

. "$RepoRoot/scripts/Get-ZsPackages.ps1"

# Discovered and topologically sorted so each package installs after its local dependencies.
foreach ($pkg in Get-ZsPackages -PackagesRoot (Join-Path $RepoRoot 'packages')) {
    Write-Host "=== Installing $($pkg.Name) ==="
    dotnet run --no-build --project "$RepoRoot/src/ZScheme.Cli" -- `
        install -m $pkg.Manifest @DebugArgs
    if ($LASTEXITCODE -ne 0) { throw "Installing $($pkg.Name) failed" }
}
