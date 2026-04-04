#!/usr/bin/env pwsh
param(
    [string[]]$Examples = @(),
    [switch]$Debug,
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
$TempDir = $null
# Cache root must match ZSchemePaths.GetPackageCacheRoot() in the compiler
$CacheRoot = Join-Path $HOME ".zscheme\cache\pkg"

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
        return ,@()
    }

    $results = @($InputItems | ForEach-Object -Parallel $ScriptBlock -ThrottleLimit $ThrottleLimit)

    # Print in sorted order
    foreach ($r in ($results | Sort-Object Name)) {
        if ($r.Success) {
            Write-Host "  $($r.Name) ... OK"
        } else {
            $msg = if ($r.ErrorOutput) { "FAIL" } else { "FAIL (no project generated)" }
            Write-Host "  $($r.Name) ... $msg"
            if ($r.ErrorOutput) {
                $r.ErrorOutput -split "`n" | ForEach-Object { Write-Host "    $_" }
            }
        }
    }

    $passed = @($results | Where-Object Success).Count
    $total = $results.Count
    Write-Host "  $passed/$total passed"

    return ,$results
}

try {
    # ==================================================================
    # One-time setup
    # ==================================================================
    Write-Host "=== Building solution ==="
    dotnet build "$RepoRoot/ZScheme.slnx" --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Solution build failed" }

    $installArgs = if ($Debug) { @('-Debug') } else { @() }
    & "$RepoRoot/install-packages.ps1" @installArgs
    if ($LASTEXITCODE -ne 0) { throw "Installing packages failed" }

    $TempDir = Join-Path ([System.IO.Path]::GetTempPath()) "zscheme-verify-$([System.Guid]::NewGuid().ToString('N').Substring(0,8))"
    New-Item -ItemType Directory -Path $TempDir -Force | Out-Null

    # Clean output directory
    $OutDir = Join-Path $RepoRoot "examples/out"
    if (Test-Path $OutDir) {
        Remove-Item $OutDir -Recurse -Force
    }

    # Discover and filter example files once
    $AllExampleFiles = @(Get-ChildItem "$RepoRoot/examples/*.zs")
    if ($Examples.Count -gt 0) {
        $AllExampleFiles = @($AllExampleFiles | Where-Object { $_.BaseName -in $Examples })
    }

    Write-Host "=== Running with ThrottleLimit=$ThrottleLimit ==="

    $TranspileDir = Join-Path $OutDir "transpile"
    $CscDir = Join-Path $OutDir "csc"
    $IlDir = Join-Path $OutDir "il"
    New-Item -ItemType Directory -Path $TranspileDir -Force | Out-Null
    New-Item -ItemType Directory -Path $CscDir -Force | Out-Null
    New-Item -ItemType Directory -Path $IlDir -Force | Out-Null

    # ZUnit precompiled assembly from package cache
    # Find latest installed zunit version
    $ZunitVersionDir = Get-ChildItem (Join-Path $CacheRoot "zscheme-zunit") -Directory |
        Where-Object { $_.Name -match '^\d+\.\d+\.\d+' } |
        Sort-Object { [Version]$_.Name } -Descending |
        Select-Object -First 1
    if (-not $ZunitVersionDir) { throw "zscheme-zunit is not installed. Run 'zs install' first." }
    $ZunitArgs = @('--precompiled', (Join-Path $ZunitVersionDir.FullName "zscheme-zunit.dll"))

    # ==============================================================
    # Phase 1: ZScheme -> C# Transpile (emit project)
    # ==============================================================
    $transpileResults = Invoke-PhaseParallel `
        -PhaseLabel "Phase 1: ZScheme -> C# Transpile" `
        -InputItems $AllExampleFiles `
        -ThrottleLimit $ThrottleLimit `
        -ScriptBlock {
            $zsFile = $_
            $name = $zsFile.BaseName
            $errFile = Join-Path $using:TempDir "stderr-transpile-$name.log"
            $projectOut = Join-Path $using:TranspileDir $name

            $ErrorActionPreference = 'Continue'
            $output = dotnet run --no-build --project "$using:RepoRoot/src/ZScheme.Cli" -- `
                compile $zsFile.FullName `
                @using:ZunitArgs `
                --emit-project --output-type Library --lang-version preview `
                --nuget xunit:2.9.3 `
                -o $projectOut @using:DebugArgs 2>$errFile
            $exitCode = $LASTEXITCODE

            $errText = $null
            if ($exitCode -ne 0) {
                if (Test-Path $errFile) {
                    $errText = ((Get-Content $errFile -Raw) ?? '').Trim()
                }
                if (Test-Path $projectOut) { Remove-Item $projectOut -Recurse -ErrorAction SilentlyContinue }
                [PSCustomObject]@{ Name = $name; Success = $false; ErrorOutput = $errText }
            } elseif (-not (Test-Path (Join-Path $projectOut "$name.csproj"))) {
                [PSCustomObject]@{ Name = $name; Success = $false; ErrorOutput = $null }
            } else {
                [PSCustomObject]@{ Name = $name; Success = $true; ErrorOutput = $null }
            }
        }

    $transpileSucceededNames = @($transpileResults | Where-Object Success | ForEach-Object Name)
    $transpilePassed = @($transpileResults | Where-Object Success).Count
    $transpileFailed = @($transpileResults | Where-Object { -not $_.Success }).Count

    # ==============================================================
    # Phase 2: C# Compile (csc)
    # ==============================================================
    $cscResults = Invoke-PhaseParallel `
        -PhaseLabel "Phase 2: C# Compile (csc)" `
        -InputItems $transpileSucceededNames `
        -ThrottleLimit $ThrottleLimit `
        -ScriptBlock {
            $name = $_
            $errFile = Join-Path $using:TempDir "stderr-csc-$name.log"

            $ErrorActionPreference = 'Continue'
            $output = dotnet build (Join-Path $using:TranspileDir "$name/$name.csproj") --nologo -v quiet 2>$errFile
            $exitCode = $LASTEXITCODE

            if ($exitCode -eq 0) {
                Copy-Item (Join-Path $using:TranspileDir "$name/bin/Debug/net10.0/$name.dll") (Join-Path $using:CscDir "$name.dll")
                [PSCustomObject]@{ Name = $name; Success = $true; ErrorOutput = $null }
            } else {
                $errText = $null
                if (Test-Path $errFile) {
                    $errText = ((Get-Content $errFile -Raw) ?? '').Trim()
                }
                [PSCustomObject]@{ Name = $name; Success = $false; ErrorOutput = $errText }
            }
        }

    $cscPassed = @($cscResults | Where-Object Success).Count
    $cscFailed = @($cscResults | Where-Object { -not $_.Success }).Count

    # ==============================================================
    # Phase 3: ZScheme -> IL Direct Compile
    # ==============================================================
    $ilResults = Invoke-PhaseParallel `
        -PhaseLabel "Phase 3: ZScheme -> IL Direct Compile" `
        -InputItems $AllExampleFiles `
        -ThrottleLimit $ThrottleLimit `
        -ScriptBlock {
            $zsFile = $_
            $name = $zsFile.BaseName
            $errFile = Join-Path $using:TempDir "stderr-il-$name.log"
            # Each example gets its own subdirectory to avoid file contention
            # when the compiler copies precompiled assemblies (e.g. zscheme-stdlib.dll)
            $ilSubDir = Join-Path $using:IlDir $name
            New-Item -ItemType Directory -Path $ilSubDir -Force | Out-Null
            $ilOut = Join-Path $ilSubDir "$name.dll"

            $ErrorActionPreference = 'Continue'
            $output = dotnet run --no-build --project "$using:RepoRoot/src/ZScheme.Cli" -- `
                compile $zsFile.FullName --backend il `
                @using:ZunitArgs `
                --nuget xunit:2.9.3 `
                -o $ilOut @using:DebugArgs 2>$errFile
            $exitCode = $LASTEXITCODE

            if ($exitCode -eq 0) {
                [PSCustomObject]@{ Name = $name; Success = $true; ErrorOutput = $null }
            } else {
                $errText = $null
                if (Test-Path $errFile) {
                    $errText = ((Get-Content $errFile -Raw) ?? '').Trim()
                }
                [PSCustomObject]@{ Name = $name; Success = $false; ErrorOutput = $errText }
            }
        }

    $ilPassed = @($ilResults | Where-Object Success).Count
    $ilFailed = @($ilResults | Where-Object { -not $_.Success }).Count

    # ==============================================================
    # Summary
    # ==============================================================
    $totalTranspile = $transpilePassed + $transpileFailed
    $totalCsc = $cscPassed + $cscFailed
    $totalIl = $ilPassed + $ilFailed

    Write-Host ""
    Write-Host "========================================"
    Write-Host "=== Summary ==="
    Write-Host "========================================"
    Write-Host "  ZScheme -> C# Transpile:  $transpilePassed/$totalTranspile passed"
    Write-Host "  C# Compile (csc):         $cscPassed/$totalCsc passed"
    Write-Host "  IL Direct Compile:         $ilPassed/$totalIl passed"

    $totalFailed = $transpileFailed + $cscFailed + $ilFailed
    $totalPassed = $transpilePassed + $cscPassed + $ilPassed
    Write-Host ""
    Write-Host "  Total: $totalPassed passed, $totalFailed failed"

    if ($totalFailed -gt 0) {
        exit 1
    }
} finally {
    if ($TempDir -and (Test-Path $TempDir)) {
        Remove-Item $TempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
