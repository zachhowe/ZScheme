#!/usr/bin/env pwsh
param(
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

function Update-XmlVersion {
    param([string]$Path)
    [xml]$xml = Get-Content $Path
    $old = ($xml.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ }).Trim()
    ($xml.Project.PropertyGroup | Where-Object { $_.Version }) | ForEach-Object { $_.Version = $Version }
    $xml.Save((Resolve-Path $Path))
    Write-Host "  $Path"
    Write-Host "    $old -> $Version"
}

function Update-JsonVersion {
    param([string]$Path)
    $json = Get-Content $Path -Raw | ConvertFrom-Json
    $old = $json.version
    $json.version = $Version
    $json | ConvertTo-Json -Depth 32 | Set-Content $Path -NoNewline
    Write-Host "  $Path"
    Write-Host "    $old -> $Version"
}

function Update-ZspkgVersion {
    param([string]$Path)
    $content = Get-Content $Path -Raw
    $old = if ($content -match '\(version\s+"([^"]+)"\)') { $Matches[1] } else { '?' }
    $updated = $content -replace '\(version\s+"[^"]+"\)', "(version ""$Version"")"
    Set-Content $Path $updated -NoNewline
    Write-Host "  $Path"
    Write-Host "    $old -> $Version"
}

Write-Host "Bumping all versions to $Version"
Write-Host ""

# 1. Directory.Build.props (all .NET projects)
Update-XmlVersion "$RepoRoot/Directory.Build.props"

# 2. Editor package.json files
Update-JsonVersion "$RepoRoot/editor/vscode/package.json"
Update-JsonVersion "$RepoRoot/editor/zed/tree-sitter-zscheme/package.json"

# 3. ZScheme package manifests
Update-ZspkgVersion "$RepoRoot/packages/stdlib/package.zspkg"
Update-ZspkgVersion "$RepoRoot/packages/zunit/package.zspkg"
Update-ZspkgVersion "$RepoRoot/packages/http/package.zspkg"

Write-Host ""
Write-Host "Done. Updated 6 files."
