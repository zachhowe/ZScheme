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

function Update-ManifestVersion {
    param([string]$Path)
    $content = Get-Content $Path -Raw
    $match = [regex]::Match($content, '\(version\s+"([^"]+)"\)')
    if (-not $match.Success) {
        Write-Error "Could not find (version ""..."") in $Path"
        exit 1
    }
    $old = $match.Groups[1].Value
    $newContent = $content -replace '\(version\s+"[^"]+"\)', "(version ""$Version"")"
    Set-Content $Path -Value $newContent -NoNewline
    Write-Host "  $Path"
    Write-Host "    $old -> $Version"
}

Write-Host "Bumping ZScheme version to $Version"
Write-Host ""

# 1. Directory.Build.props (all .NET projects)
Update-XmlVersion "$RepoRoot/Directory.Build.props"

# 2. Editor package.json files
Update-JsonVersion "$RepoRoot/editor/vscode/package.json"
Update-JsonVersion "$RepoRoot/editor/zed/tree-sitter-zscheme/package.json"

# 3. Every package manifest. Package versions are kept in sync with the compiler
#    version while the packages live in this repo -- see CLAUDE.md.
$manifests = Get-ChildItem "$RepoRoot/packages" -Directory |
    ForEach-Object { Join-Path $_.FullName 'package.zspkg' } |
    Where-Object { Test-Path $_ } |
    Sort-Object
foreach ($manifest in $manifests) {
    Update-ManifestVersion $manifest
}

Write-Host ""
Write-Host "Done. Updated $(3 + $manifests.Count) files ($($manifests.Count) package manifests)."
