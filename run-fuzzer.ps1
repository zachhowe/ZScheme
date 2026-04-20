#!/usr/bin/env pwsh
param(
    [long]$Seed = 0,
    [int]$Iterations = 1000,
    [int]$MaxDepth = 5,
    [int]$MaxFuncs = 3,
    [string[]]$Oracles = @('compile','ilverify','diffexec'),
    [switch]$KeepPassing,
    [switch]$Verbose,
    [double]$Timeout = 10,
    [string]$OutputDir,
    [switch]$AllowOldPowerShellVersionsAndRiskFailingScripts
)

if ($PSVersionTable.PSVersion -lt [Version]'7.6.0' -and -not $AllowOldPowerShellVersionsAndRiskFailingScripts) {
    Write-Error "This script requires PowerShell 7.6.0 or newer (pwsh). Current version: $($PSVersionTable.PSVersion). Pass -AllowOldPowerShellVersionsAndRiskFailingScripts to override."
    exit 1
}

$ErrorActionPreference = 'Stop'
$RepoRoot = $PSScriptRoot
$failures = 0
$results = @()

function Run-Step {
    param([string]$Label, [scriptblock]$Action)
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

Run-Step "dotnet tool restore" {
    dotnet tool restore
}

Run-Step "dotnet build (fuzzer)" {
    dotnet build "$RepoRoot/src/ZScheme.Fuzzer/ZScheme.Fuzzer.csproj" --nologo
}

if ($failures -gt 0) {
    Write-Host ""
    Write-Host "Build or tool restore failed; aborting fuzz run."
    foreach ($r in $results) { Write-Host "  $r" }
    exit 1
}

$fuzzerArgs = @('--iterations', $Iterations, '--max-depth', $MaxDepth, '--max-funcs', $MaxFuncs,
                '--oracles', ($Oracles -join ','), '--timeout', $Timeout)
if ($Seed -ne 0)       { $fuzzerArgs += @('--seed', $Seed) }
if ($KeepPassing)      { $fuzzerArgs += @('--keep-passing') }
if ($Verbose)          { $fuzzerArgs += @('--verbose') }
if ($OutputDir)        { $fuzzerArgs += @('--output-dir', $OutputDir) }

Run-Step "fuzz" {
    dotnet run --no-build --project "$RepoRoot/src/ZScheme.Fuzzer" -- @fuzzerArgs
}

Write-Host ""
Write-Host "================================================================"
Write-Host "=== Summary ==="
Write-Host "================================================================"
foreach ($r in $results) { Write-Host "  $r" }
Write-Host ""
if ($failures -gt 0) { exit 1 } else { exit 0 }
