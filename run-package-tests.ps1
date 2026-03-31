#!/usr/bin/env pwsh
param(
    [switch]$Debug
)

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
        test -m "$RepoRoot/packages/stdlib/package.zspkg" `
        --module-path "$RepoRoot/packages/zunit/src" `
        --package-path "$RepoRoot/packages/zunit" @DebugArgs
}

# Rebuild stdlib package cache so dependent packages (http) pick up latest changes
dotnet run --no-build --project "$RepoRoot/src/ZScheme.Cli" -- `
    install --manifest "$RepoRoot/packages/stdlib/package.zspkg" 2>&1 | Out-Null

Run-Step "http tests" {
    dotnet run --no-build --project "$RepoRoot/src/ZScheme.Cli" -- `
        test -m "$RepoRoot/packages/http/package.zspkg" `
        --module-path "$RepoRoot/packages/zunit/src" `
        --package-path "$RepoRoot/packages/zunit" `
        --module-path "$RepoRoot/packages/stdlib/src" `
        --package-path "$RepoRoot/packages/stdlib" @DebugArgs
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
