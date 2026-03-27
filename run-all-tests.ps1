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

Run-Step "install packages" {
    & "$RepoRoot/install-packages.ps1"
}

Run-Step "package tests" {
    & "$RepoRoot/run-package-tests.ps1"
}

Run-Step "build-examples" {
    & "$RepoRoot/build-examples.ps1"
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
