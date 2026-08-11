#!/usr/bin/env pwsh
param(
    [Parameter(Mandatory)]
    [string]$Package,
    [Parameter(Mandatory)]
    [string]$Version,
    [switch]$AllowOldPowerShellVersionsAndRiskFailingScripts
)

if ($PSVersionTable.PSVersion -lt [Version]'7.6.0' -and -not $AllowOldPowerShellVersionsAndRiskFailingScripts) {
    Write-Error "This script requires PowerShell 7.6.0 or newer (pwsh). Current version: $($PSVersionTable.PSVersion). Pass -AllowOldPowerShellVersionsAndRiskFailingScripts to override."
    exit 1
}

$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^\d+\.\d+\.\d+(-[\w.]+)?$') {
    Write-Error "Invalid version '$Version'. Expected format: X.Y.Z or X.Y.Z-prerelease"
    exit 1
}

$RepoRoot = $PSScriptRoot
$ManifestPath = "$RepoRoot/packages/$Package/package.zspkg"

if (-not (Test-Path $ManifestPath)) {
    $available = Get-ChildItem "$RepoRoot/packages" -Directory | ForEach-Object { $_.Name }
    Write-Error "Package '$Package' not found. Available packages: $($available -join ', ')"
    exit 1
}

$content = Get-Content $ManifestPath -Raw
$match = [regex]::Match($content, '\(version\s+"([^"]+)"\)')
if (-not $match.Success) {
    Write-Error "Could not find (version ""..."") in $ManifestPath"
    exit 1
}

$oldVersion = $match.Groups[1].Value
$newContent = $content -replace '\(version\s+"[^"]+"\)', "(version ""$Version"")"
Set-Content $ManifestPath -Value $newContent -NoNewline

Write-Host "Bumped package '$Package' version"
Write-Host "  $ManifestPath"
Write-Host "    $oldVersion -> $Version"

# Package versions are kept in sync with the compiler version while the packages live in
# this repo (see CLAUDE.md), so a single-package bump is normally the wrong tool: use
# ./bump-version.ps1, which moves the compiler, the editors and every manifest together.
$compilerVersion = ([xml](Get-Content "$RepoRoot/Directory.Build.props")).Project.PropertyGroup |
    ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1
if ($compilerVersion -and $compilerVersion.Trim() -ne $Version) {
    Write-Host ""
    Write-Warning "Package '$Package' is now $Version but the compiler is $($compilerVersion.Trim())."
    Write-Warning "These are meant to stay in sync -- prefer ./bump-version.ps1 $Version."
}
