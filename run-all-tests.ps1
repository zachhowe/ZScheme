#!/usr/bin/env pwsh
param(
    [switch]$AllowOldPowerShellVersionsAndRiskFailingScripts
)

if ($PSVersionTable.PSVersion -lt [Version]'7.6.0' -and -not $AllowOldPowerShellVersionsAndRiskFailingScripts) {
    Write-Error "This script requires PowerShell 7.6.0 or newer (pwsh). Current version: $($PSVersionTable.PSVersion). Pass -AllowOldPowerShellVersionsAndRiskFailingScripts to override."
    exit 1
}

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
    dotnet build "$RepoRoot/ZScheme.slnx" --nologo
}

Run-Step "dotnet test" {
    dotnet test "$RepoRoot/ZScheme.slnx" --no-build --nologo `
        --collect:"XPlat Code Coverage" `
        --results-directory "$PSScriptRoot/coverage" `
}

Run-Step "install packages" {
    & "$RepoRoot/install-packages.ps1"
}

Run-Step "package tests" {
    & "$RepoRoot/run-package-tests.ps1"
}

Run-Step "package tests (C# backend)" {
    # -NoSetup: the solution build and package install already ran as steps above.
    & "$RepoRoot/run-package-csharp-tests.ps1" -NoSetup
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
