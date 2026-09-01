#!/usr/bin/env pwsh
param(
    [switch]$Debug,
    [switch]$AllowOldPowerShellVersionsAndRiskFailingScripts
)

if ($PSVersionTable.PSVersion -lt [Version]'7.6.0' -and -not $AllowOldPowerShellVersionsAndRiskFailingScripts) {
    Write-Error "This script requires PowerShell 7.6.0 or newer (pwsh). Current version: $($PSVersionTable.PSVersion). Pass -AllowOldPowerShellVersionsAndRiskFailingScripts to override."
    exit 1
}

$ErrorActionPreference = 'Continue'

$RepoRoot = $PSScriptRoot
$DebugArgs = if ($Debug) { @('--debug') } else { @() }
$failures = 0
$results = @()

function Run-Step {
    param(
        [string]$Label,
        [scriptblock]$Action
    )

    Write-Host ""
    Write-Host "================================================================"
    Write-Host "=== $Label ==="
    Write-Host "================================================================"

    & $Action
    if ($LASTEXITCODE -eq 0) {
        $script:results += "PASS: $Label"
    } else {
        $script:results += "FAIL: $Label"
        $script:failures++
    }
}

. "$RepoRoot/scripts/Get-ZsPackages.ps1"

# Dependency order, and the installs between steps, both come from Get-ZsPackages rather than
# being written out by hand. Every package is installed before the packages that depend on it
# are tested, because a dependency is now *referenced* by its dependents rather than compiled
# into them: what `zs test` binds against is the artifact in the cache, so a package whose
# sources changed has to be reinstalled before anything downstream is tested. The hand-written
# list this replaces had drifted — zunit was never installed here at all, and the step before
# the aspnet tests installed aspnet rather than http.
$packages = Get-ZsPackages -PackagesRoot "$RepoRoot/packages"

foreach ($pkg in $packages) {
    Write-Host ""
    Write-Host "=== Installing $($pkg.Name) ==="
    dotnet run --no-build --project "$RepoRoot/src/ZScheme.Cli" -- `
        install --manifest $pkg.Manifest
    if ($LASTEXITCODE -ne 0) {
        # Previously discarded with `2>&1 | Out-Null` and never checked, so a dependency that
        # failed to install showed up only as an unexplained downstream test failure.
        $results += "FAIL: install $($pkg.Name)"
        $failures++
        continue
    }

    if (-not $pkg.HasTests) { continue }

    Run-Step "$($pkg.Name) tests" {
        dotnet run --no-build --project "$RepoRoot/src/ZScheme.Cli" -- `
            test -m $pkg.Manifest @DebugArgs
    }
}

Write-Host ""
Write-Host "================================================================"
Write-Host "=== Summary ==="
Write-Host "================================================================"
foreach ($r in $results) {
    Write-Host "  $r"
}
Write-Host ""

if ($failures -gt 0) {
    Write-Host "$failures step(s) failed."
    exit 1
} else {
    Write-Host "All steps passed."
}
