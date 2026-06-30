#!/usr/bin/env pwsh
param(
    [switch]$Check,
    [switch]$AllowOldPowerShellVersionsAndRiskFailingScripts
)

if ($PSVersionTable.PSVersion -lt [Version]'7.6.0' -and -not $AllowOldPowerShellVersionsAndRiskFailingScripts) {
    Write-Error "This script requires PowerShell 7.6.0 or newer (pwsh). Current version: $($PSVersionTable.PSVersion). Pass -AllowOldPowerShellVersionsAndRiskFailingScripts to override."
    exit 1
}

$ErrorActionPreference = 'Stop'

$RepoRoot = $PSScriptRoot
Push-Location $RepoRoot
try {
    # Ensure the CSharpier local tool is available (declared in .config/dotnet-tools.json).
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) {
        Write-Error "'dotnet tool restore' failed; cannot run CSharpier."
        exit 1
    }

    if ($Check) {
        Write-Host "Checking C# formatting with CSharpier..."
        dotnet tool run csharpier -- check .
    } else {
        Write-Host "Formatting all C# files with CSharpier..."
        dotnet tool run csharpier -- format .
    }
    exit $LASTEXITCODE
} finally {
    Pop-Location
}
