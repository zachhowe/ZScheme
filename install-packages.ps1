#!/usr/bin/env pwsh
param(
    [switch]$Debug,
    [switch]$AllowOldPowerShellVersionsAndRiskFailingScripts
)

if ($PSVersionTable.PSVersion -lt [Version]'7.6.0' -and -not $AllowOldPowerShellVersionsAndRiskFailingScripts) {
    Write-Error "This script requires PowerShell 7.6.0 or newer (pwsh). Current version: $($PSVersionTable.PSVersion). Pass -AllowOldPowerShellVersionsAndRiskFailingScripts to override."
    exit 1
}

$ErrorActionPreference = 'Stop'

$RepoRoot = $PSScriptRoot
$DebugArgs = if ($Debug) { @('--debug') } else { @() }
$PackagesRoot = Join-Path $RepoRoot 'packages'

# Discover every top-level package: a packages/<name>/package.zspkg manifest.
$packages = @{}
foreach ($dir in Get-ChildItem -Path $PackagesRoot -Directory) {
    $manifest = Join-Path $dir.FullName 'package.zspkg'
    if (-not (Test-Path $manifest)) { continue }
    # Local zscheme dependencies are referenced as `:local "../<name>"`.
    $deps = [regex]::Matches((Get-Content -Raw $manifest), ':local\s+"\.\./([^"]+)"') |
        ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
    $packages[$dir.Name] = [pscustomobject]@{ Name = $dir.Name; Manifest = $manifest; Deps = $deps }
}

# Topologically sort so each package installs after its local dependencies.
$ordered = [System.Collections.Generic.List[object]]::new()
$visited = @{}
function Visit($name) {
    if ($visited.ContainsKey($name)) {
        if ($visited[$name] -eq 'visiting') { throw "Dependency cycle detected at package '$name'" }
        return
    }
    $visited[$name] = 'visiting'
    foreach ($dep in $packages[$name].Deps) {
        if ($packages.ContainsKey($dep)) { Visit $dep }
    }
    $ordered.Add($packages[$name])
    $visited[$name] = 'done'
}
foreach ($name in $packages.Keys | Sort-Object) { Visit $name }

foreach ($pkg in $ordered) {
    Write-Host "=== Installing $($pkg.Name) ==="
    dotnet run --no-build --project "$RepoRoot/src/ZScheme.Cli" -- `
        install -m $pkg.Manifest @DebugArgs
    if ($LASTEXITCODE -ne 0) { throw "Installing $($pkg.Name) failed" }
}
