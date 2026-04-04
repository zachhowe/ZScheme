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

Write-Host "=== Installing stdlib ==="
dotnet run --no-build --project "$RepoRoot/src/ZScheme.Cli" -- `
    install -m "$RepoRoot/packages/stdlib/package.zspkg" @DebugArgs
if ($LASTEXITCODE -ne 0) { throw "Installing stdlib failed" }

Write-Host "=== Installing ZUnit ==="
dotnet run --no-build --project "$RepoRoot/src/ZScheme.Cli" -- `
    install -m "$RepoRoot/packages/zunit/package.zspkg" @DebugArgs
if ($LASTEXITCODE -ne 0) { throw "Installing ZUnit failed" }

Write-Host "=== Installing http ==="
dotnet run --no-build --project "$RepoRoot/src/ZScheme.Cli" -- `
    install -m "$RepoRoot/packages/http/package.zspkg" @DebugArgs
if ($LASTEXITCODE -ne 0) { throw "Installing http failed" }
