#!/usr/bin/env pwsh
# Dot-source this file to get Get-ZsPackages. Shared by install-packages.ps1 and
# run-package-csharp-tests.ps1 so there is one definition of "what the packages are and
# what order they go in".

function Get-ZsPackages {
    <#
    .SYNOPSIS
        Discovers every packages/<name>/package.zspkg and returns them in dependency order.
    .DESCRIPTION
        Local ZScheme dependencies are declared as `:local "../<name>"`, which is enough to
        topologically sort the set without parsing the whole manifest. Each returned object
        has Name, Manifest, Deps, and HasTests.
    .PARAMETER PackagesRoot
        The packages/ directory to scan.
    .PARAMETER Only
        Optional package names to keep. Filtering happens after sorting, so the surviving
        packages stay in dependency order.
    #>
    param(
        [Parameter(Mandatory)][string]$PackagesRoot,
        [string[]]$Only = @()
    )

    $packages = @{}
    foreach ($dir in Get-ChildItem -Path $PackagesRoot -Directory) {
        $manifest = Join-Path $dir.FullName 'package.zspkg'
        if (-not (Test-Path $manifest)) { continue }
        $deps = [regex]::Matches((Get-Content -Raw $manifest), ':local\s+"\.\./([^"]+)"') |
            ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
        $packages[$dir.Name] = [pscustomobject]@{
            Name     = $dir.Name
            Dir      = $dir.FullName
            Manifest = $manifest
            Deps     = $deps
            HasTests = Test-Path (Join-Path $dir.FullName 'test')
        }
    }

    $ordered = [System.Collections.Generic.List[object]]::new()
    $visited = @{}

    function Visit-Package($name) {
        if ($visited.ContainsKey($name)) {
            if ($visited[$name] -eq 'visiting') { throw "Dependency cycle detected at package '$name'" }
            return
        }
        $visited[$name] = 'visiting'
        foreach ($dep in $packages[$name].Deps) {
            if ($packages.ContainsKey($dep)) { Visit-Package $dep }
        }
        $ordered.Add($packages[$name])
        $visited[$name] = 'done'
    }

    foreach ($name in $packages.Keys | Sort-Object) { Visit-Package $name }

    if ($Only.Count -gt 0) {
        return @($ordered | Where-Object { $_.Name -in $Only })
    }
    return @($ordered)
}
