#!/usr/bin/env pwsh
$ErrorActionPreference = 'Continue'

$RepoRoot = $PSScriptRoot
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

Run-Step "dotnet build" {
    dotnet build "$RepoRoot/ZScript.slnx" --nologo
}

Run-Step "dotnet test" {
    dotnet test "$RepoRoot/ZScript.slnx" --no-build --nologo
}

Run-Step "stdlib tests" {
    dotnet run --no-build --project "$RepoRoot/src/ZScript.Cli" -- `
        test -m "$RepoRoot/packages/stdlib/package.zspkg" `
        --module-path "$RepoRoot/packages/zunit/src"
}

Run-Step "build-examples (source stdlib, source zunit)" {
    & "$RepoRoot/build-examples.ps1"
}

Run-Step "build-examples (cached stdlib, source zunit)" {
    & "$RepoRoot/build-examples.ps1" -CachedStdlib
}

Run-Step "build-examples (source stdlib, cached zunit)" {
    & "$RepoRoot/build-examples.ps1" -CachedZunit
}

Run-Step "build-examples (cached stdlib, cached zunit)" {
    & "$RepoRoot/build-examples.ps1" -CachedStdlib -CachedZunit
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
