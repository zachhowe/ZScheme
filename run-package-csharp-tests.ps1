#!/usr/bin/env pwsh
# Builds every package under the C# backend and runs the generated C# tests, then compiles
# each package with the IL backend as well. The IL *tests* are run by run-package-tests.ps1;
# this script is the C#-backend half of package validation, plus an IL compile so both
# backends are exercised over the same sources in one place.
#
# Output mirrors examples/out so each artifact can be inspected by hand:
#   packages/out/transpile/<pkg>/   the generated C# solution (main + .Tests projects)
#   packages/out/csc/<pkg>/         the assemblies csc produced from it
#   packages/out/il/<pkg>/          the IL backend's assembly for the same package
param(
    [string[]]$Packages = @(),
    [switch]$Debug,
    [switch]$NoSetup,
    [switch]$KeepOutput,
    [int]$ThrottleLimit = [Environment]::ProcessorCount,
    [switch]$Sequential,
    [switch]$AllowOldPowerShellVersionsAndRiskFailingScripts
)

if ($PSVersionTable.PSVersion -lt [Version]'7.6.0' -and -not $AllowOldPowerShellVersionsAndRiskFailingScripts) {
    Write-Error "This script requires PowerShell 7.6.0 or newer (pwsh). Current version: $($PSVersionTable.PSVersion). Pass -AllowOldPowerShellVersionsAndRiskFailingScripts to override."
    exit 1
}

$ErrorActionPreference = 'Stop'

if ($Sequential) {
    $ThrottleLimit = 1
}

$RepoRoot = $PSScriptRoot
$DebugArgs = if ($Debug) { @('--debug') } else { @() }
$OutRoot = Join-Path $RepoRoot 'packages/out'
$TranspileDir = Join-Path $OutRoot 'transpile'
$CscDir = Join-Path $OutRoot 'csc'
$IlDir = Join-Path $OutRoot 'il'
$LogDir = $null

. "$RepoRoot/scripts/Get-ZsPackages.ps1"

function Invoke-PhaseParallel {
    param(
        [string]$PhaseLabel,
        [object[]]$InputItems,
        [scriptblock]$ScriptBlock,
        [int]$ThrottleLimit
    )

    Write-Host ""
    Write-Host "=== $PhaseLabel ==="

    if ($InputItems.Count -eq 0) {
        Write-Host "  (no items)"
        return , @()
    }

    $results = @($InputItems | ForEach-Object -Parallel $ScriptBlock -ThrottleLimit $ThrottleLimit)

    foreach ($r in ($results | Sort-Object Name)) {
        if ($r.Skipped) {
            Write-Host "  $($r.Name) ... SKIP ($($r.Detail))"
        } elseif ($r.Success) {
            $detail = if ($r.Detail) { " ($($r.Detail))" } else { "" }
            Write-Host "  $($r.Name) ... OK$detail"
        } else {
            Write-Host "  $($r.Name) ... FAIL"
            if ($r.ErrorOutput) {
                $r.ErrorOutput -split "`n" | ForEach-Object { Write-Host "    $_" }
            }
        }
    }

    $passed = @($results | Where-Object { $_.Success }).Count
    Write-Host "  $passed/$($results.Count) passed"

    return , $results
}

try {
    # ==================================================================
    # Phase 0: setup (sequential)
    # ==================================================================
    if (-not $NoSetup) {
        Write-Host "=== Building solution ==="
        dotnet build "$RepoRoot/ZScheme.slnx" --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "Solution build failed" }

        $installArgs = if ($Debug) { @('-Debug') } else { @() }
        & "$RepoRoot/install-packages.ps1" @installArgs
        if ($LASTEXITCODE -ne 0) { throw "Installing packages failed" }
    }

    $selected = Get-ZsPackages -PackagesRoot (Join-Path $RepoRoot 'packages') -Only $Packages
    if ($selected.Count -eq 0) {
        Write-Host "No packages matched."
        exit 1
    }

    # Wipe each selected package's output so what is left behind is always exactly what this
    # run produced — a stale .cs from a previous run is still globbed by the SDK.
    if (-not $KeepOutput) {
        foreach ($pkg in $selected) {
            foreach ($root in @($TranspileDir, $CscDir, $IlDir)) {
                $dir = Join-Path $root $pkg.Name
                if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
            }
        }
    }
    foreach ($root in @($TranspileDir, $CscDir, $IlDir)) {
        New-Item -ItemType Directory -Path $root -Force | Out-Null
    }

    $LogDir = Join-Path ([System.IO.Path]::GetTempPath()) "zscheme-pkgcs-$([System.Guid]::NewGuid().ToString('N').Substring(0,8))"
    New-Item -ItemType Directory -Path $LogDir -Force | Out-Null

    Write-Host "=== Running with ThrottleLimit=$ThrottleLimit ==="
    Write-Host "=== Output: $OutRoot ==="

    # ==============================================================
    # Phase 1: ZScheme -> C# project (zs generate-project)
    # ==============================================================
    $transpileResults = Invoke-PhaseParallel `
        -PhaseLabel "Phase 1: ZScheme -> C# Transpile" `
        -InputItems $selected `
        -ThrottleLimit $ThrottleLimit `
        -ScriptBlock {
            . "$using:RepoRoot/scripts/Format-ZsDiagnostics.ps1"
            $pkg = $_
            $outDir = Join-Path $using:TranspileDir $pkg.Name
            $logPath = Join-Path $using:LogDir "transpile-$($pkg.Name).log"

            $ErrorActionPreference = 'Continue'
            dotnet run --no-build --project "$using:RepoRoot/src/ZScheme.Cli" -- `
                generate-project -m $pkg.Manifest -o $outDir @using:DebugArgs *> $logPath
            $exitCode = $LASTEXITCODE

            $slnx = @(Get-ChildItem -Path $outDir -Filter '*.slnx' -ErrorAction SilentlyContinue)
            $testProj = @(Get-ChildItem -Path $outDir -Filter '*.Tests' -Directory -ErrorAction SilentlyContinue)

            [PSCustomObject]@{
                Name           = $pkg.Name
                Success        = ($exitCode -eq 0 -and $slnx.Count -gt 0)
                Skipped        = $false
                Detail         = $null
                Solution       = if ($slnx.Count -gt 0) { $slnx[0].FullName } else { $null }
                OutDir         = $outDir
                HasTestProject = $testProj.Count -gt 0
                HasTestSources = $pkg.HasTests
                ErrorOutput    = if ($exitCode -eq 0 -and $slnx.Count -gt 0) { $null }
                                 else { (Format-ZsDiagnostics $logPath) }
            }
        }

    # ==============================================================
    # Phase 2: C# Compile (csc)
    # ==============================================================
    $cscResults = Invoke-PhaseParallel `
        -PhaseLabel "Phase 2: C# Compile (csc)" `
        -InputItems @($transpileResults | Where-Object Success) `
        -ThrottleLimit $ThrottleLimit `
        -ScriptBlock {
            . "$using:RepoRoot/scripts/Format-ZsDiagnostics.ps1"
            $item = $_
            $logPath = Join-Path $using:LogDir "csc-$($item.Name).log"

            $ErrorActionPreference = 'Continue'
            dotnet build $item.Solution --nologo -v minimal *> $logPath
            $exitCode = $LASTEXITCODE

            if ($exitCode -eq 0) {
                # Copy the produced assemblies out so they can be inspected without digging
                # through each generated project's bin/ tree.
                $dest = Join-Path $using:CscDir $item.Name
                New-Item -ItemType Directory -Path $dest -Force | Out-Null
                Get-ChildItem -Path $item.OutDir -Recurse -Filter '*.dll' |
                    Where-Object { $_.FullName -match '[\\/]bin[\\/]' } |
                    ForEach-Object { Copy-Item $_.FullName (Join-Path $dest $_.Name) -Force }
            }

            [PSCustomObject]@{
                Name           = $item.Name
                Success        = ($exitCode -eq 0)
                Skipped        = $false
                Detail         = $null
                Solution       = $item.Solution
                HasTestProject = $item.HasTestProject
                HasTestSources = $item.HasTestSources
                ErrorOutput    = if ($exitCode -eq 0) { $null } else { (Format-ZsDiagnostics $logPath) }
            }
        }

    # ==============================================================
    # Phase 3: run the generated C# tests
    # ==============================================================
    $testResults = Invoke-PhaseParallel `
        -PhaseLabel "Phase 3: C# Test (dotnet test)" `
        -InputItems @($cscResults | Where-Object Success) `
        -ThrottleLimit $ThrottleLimit `
        -ScriptBlock {
            . "$using:RepoRoot/scripts/Format-ZsDiagnostics.ps1"
            $item = $_
            $logPath = Join-Path $using:LogDir "test-$($item.Name).log"

            if (-not $item.HasTestProject) {
                # A package with no test/ directory has nothing to run. `dotnet test` would
                # exit 0 on it either way, which is exactly why this is checked explicitly:
                # a package that *does* have tests must never pass by discovering none.
                return [PSCustomObject]@{
                    Name        = $item.Name
                    Success     = -not $item.HasTestSources
                    Skipped     = -not $item.HasTestSources
                    Detail      = 'no tests'
                    Count       = 0
                    ErrorOutput = 'Package has test sources but no test project was generated.'
                }
            }

            $ErrorActionPreference = 'Continue'
            dotnet test $item.Solution --no-build --nologo *> $logPath
            $exitCode = $LASTEXITCODE

            $summary = @(Select-String -Path $logPath -Pattern 'Failed:\s+(\d+),\s+Passed:\s+(\d+)')
            $failed = if ($summary.Count -gt 0) { [int]$summary[0].Matches[0].Groups[1].Value } else { -1 }
            $passed = if ($summary.Count -gt 0) { [int]$summary[0].Matches[0].Groups[2].Value } else { 0 }

            # Zero discovered tests is a failure, not a pass: it is what a broken discovery
            # path looks like from the outside, and `dotnet test` still exits 0.
            $ok = ($exitCode -eq 0) -and ($failed -eq 0) -and ($passed -gt 0)
            [PSCustomObject]@{
                Name        = $item.Name
                Success     = $ok
                Skipped     = $false
                Detail      = "$passed passed"
                Count       = $passed
                ErrorOutput = if ($ok) { $null } else { (Format-ZsDiagnostics $logPath) }
            }
        }

    # ==============================================================
    # Phase 4: ZScheme -> IL Direct Compile
    # ==============================================================
    $ilResults = Invoke-PhaseParallel `
        -PhaseLabel "Phase 4: ZScheme -> IL Direct Compile" `
        -InputItems $selected `
        -ThrottleLimit $ThrottleLimit `
        -ScriptBlock {
            . "$using:RepoRoot/scripts/Format-ZsDiagnostics.ps1"
            $pkg = $_
            # Each package gets its own directory: the compiler copies shared precompiled
            # assemblies (ZScheme.Runtime.dll) next to the output, which would contend.
            $outDir = Join-Path $using:IlDir $pkg.Name
            New-Item -ItemType Directory -Path $outDir -Force | Out-Null
            $logPath = Join-Path $using:LogDir "il-$($pkg.Name).log"

            $ErrorActionPreference = 'Continue'
            dotnet run --no-build --project "$using:RepoRoot/src/ZScheme.Cli" -- `
                build -m $pkg.Manifest --backend il -o (Join-Path $outDir "$($pkg.Name).dll") `
                @using:DebugArgs *> $logPath
            $exitCode = $LASTEXITCODE

            [PSCustomObject]@{
                Name        = $pkg.Name
                Success     = ($exitCode -eq 0)
                Skipped     = $false
                Detail      = $null
                ErrorOutput = if ($exitCode -eq 0) { $null } else { (Format-ZsDiagnostics $logPath) }
            }
        }

    # ==============================================================
    # Summary
    # ==============================================================
    $totalTests = (@($testResults) | Measure-Object -Property Count -Sum).Sum

    Write-Host ""
    Write-Host "========================================"
    Write-Host "=== Summary ==="
    Write-Host "========================================"
    Write-Host "  ZScheme -> C# Transpile:  $(@($transpileResults | Where-Object Success).Count)/$($transpileResults.Count) passed"
    Write-Host "  C# Compile (csc):         $(@($cscResults | Where-Object Success).Count)/$($cscResults.Count) passed"
    Write-Host "  C# Test:                  $(@($testResults | Where-Object Success).Count)/$($testResults.Count) passed ($totalTests tests)"
    Write-Host "  IL Direct Compile:        $(@($ilResults | Where-Object Success).Count)/$($ilResults.Count) passed"

    $allResults = @($transpileResults) + @($cscResults) + @($testResults) + @($ilResults)
    $failedCount = @($allResults | Where-Object { -not $_.Success }).Count
    Write-Host ""
    if ($failedCount -gt 0) {
        Write-Host "  $failedCount step(s) failed."
        exit 1
    }
    Write-Host "  All steps passed."
} finally {
    if ($LogDir -and (Test-Path $LogDir)) {
        Remove-Item $LogDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
