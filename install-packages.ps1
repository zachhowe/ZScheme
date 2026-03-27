#!/usr/bin/env pwsh
param(
    [switch]$Debug
)

$ErrorActionPreference = 'Stop'

$RepoRoot = $PSScriptRoot
$DebugArgs = if ($Debug) { @('--debug') } else { @() }

Write-Host "=== Installing stdlib ==="
dotnet run --no-build --project "$RepoRoot/src/ZScript.Cli" -- `
    install -m "$RepoRoot/packages/stdlib/package.zspkg" @DebugArgs
if ($LASTEXITCODE -ne 0) { throw "Installing stdlib failed" }

Write-Host "=== Installing ZUnit ==="
dotnet run --no-build --project "$RepoRoot/src/ZScript.Cli" -- `
    install -m "$RepoRoot/packages/zunit/package.zspkg" @DebugArgs
if ($LASTEXITCODE -ne 0) { throw "Installing ZUnit failed" }

Write-Host "=== Installing http ==="
dotnet run --no-build --project "$RepoRoot/src/ZScript.Cli" -- `
    install -m "$RepoRoot/packages/http/package.zspkg" @DebugArgs
if ($LASTEXITCODE -ne 0) { throw "Installing http failed" }
