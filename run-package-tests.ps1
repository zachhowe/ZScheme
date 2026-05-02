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

Run-Step "stdlib tests" {
    dotnet run --no-build --project "$RepoRoot/src/ZScheme.Cli" -- `
        test -m "$RepoRoot/packages/stdlib/package.zspkg" @DebugArgs
}

# Rebuild stdlib package cache so dependent packages (http) pick up latest changes
dotnet run --no-build --project "$RepoRoot/src/ZScheme.Cli" -- `
    install --manifest "$RepoRoot/packages/stdlib/package.zspkg" 2>&1 | Out-Null

Run-Step "http tests" {
    dotnet run --no-build --project "$RepoRoot/src/ZScheme.Cli" -- `
        test -m "$RepoRoot/packages/http/package.zspkg" @DebugArgs
}

# Build the C# bridge that aspnet's import-clr declarations bind to.
# (The bridge wraps ASP.NET Core APIs in unambiguous Func<HttpContext, Task>
# signatures since ZScheme can't disambiguate the RequestDelegate vs Delegate
# overloads of MapGet/Use directly.)
Run-Step "aspnet bridge build" {
    dotnet build "$RepoRoot/packages/aspnet/bridge" -c Release --nologo
}

dotnet run --no-build --project "$RepoRoot/src/ZScheme.Cli" -- `
    install --manifest "$RepoRoot/packages/aspnet/package.zspkg" 2>&1 | Out-Null

Run-Step "aspnet tests" {
    dotnet run --no-build --project "$RepoRoot/src/ZScheme.Cli" -- `
        test -m "$RepoRoot/packages/aspnet/package.zspkg" @DebugArgs
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
