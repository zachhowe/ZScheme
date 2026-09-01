#!/usr/bin/env pwsh
param(
    # Must match the configuration the caller built. `dotnet run --no-build` defaults to Debug, so
    # a Release-only build -- which is exactly what publish.ps1 does on a fresh checkout -- would
    # otherwise look for a zs that was never built.
    [string]$Configuration = "Debug",
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
    dotnet run --no-build --configuration $Configuration --project "$RepoRoot/src/ZScheme.Cli" -- `
        install -m $pkg.Manifest @DebugArgs
    if ($LASTEXITCODE -ne 0) { throw "Installing $($pkg.Name) failed" }
}
