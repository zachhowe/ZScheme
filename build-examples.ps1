#!/usr/bin/env pwsh
param(
    [string]$Combo = "",
    [string[]]$Examples = @(),
    [switch]$Debug
)

$ErrorActionPreference = 'Stop'

$RepoRoot = $PSScriptRoot
$DebugArgs = if ($Debug) { @('--debug') } else { @() }
$TempDir = $null
# Cache root must match ZScriptPaths.GetPackageCacheRoot() in the compiler
$CacheRoot = Join-Path $HOME ".zscript\cache\pkg"

# Define combinations
$Combos = @(
    @{ Name = "default";        CachedStdlib = $false; CachedZunit = $false }
    @{ Name = "cached-stdlib";  CachedStdlib = $true;  CachedZunit = $false }
    @{ Name = "cached-zunit";   CachedStdlib = $false; CachedZunit = $true  }
    @{ Name = "cached-all";     CachedStdlib = $true;  CachedZunit = $true  }
)

# Filter to single combo if requested
if ($Combo -ne "") {
    $match = $Combos | Where-Object { $_.Name -eq $Combo }
    if (-not $match) {
        $valid = ($Combos | ForEach-Object { $_.Name }) -join ", "
        Write-Error "Unknown combo: $Combo (valid: $valid)"
        exit 1
    }
    $Combos = @($match)
}

try {
    # ==================================================================
    # One-time setup
    # ==================================================================
    Write-Host "=== Building solution ==="
    dotnet build "$RepoRoot/ZScript.slnx" --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Solution build failed" }

    Write-Host "=== Packing stdlib ==="
    dotnet run --no-build --project "$RepoRoot/src/ZScript.Cli" -- `
        pack -m "$RepoRoot/packages/stdlib/package.zspkg" @DebugArgs
    if ($LASTEXITCODE -ne 0) { throw "Packing stdlib failed" }

    Write-Host "=== Packing ZUnit ==="
    dotnet run --no-build --project "$RepoRoot/src/ZScript.Cli" -- `
        pack -m "$RepoRoot/packages/zunit/package.zspkg" @DebugArgs
    if ($LASTEXITCODE -ne 0) { throw "Packing ZUnit failed" }

    $TempDir = Join-Path ([System.IO.Path]::GetTempPath()) "zscript-verify-$([System.Guid]::NewGuid().ToString('N').Substring(0,8))"
    $ProjectDir = Join-Path $TempDir "verify"
    New-Item -ItemType Directory -Path $ProjectDir -Force | Out-Null

    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>preview</LangVersion>
    <Nullable>enable</Nullable>
    <OutputType>Library</OutputType>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit" Version="2.9.3" />
  </ItemGroup>
</Project>
"@ | Set-Content (Join-Path $ProjectDir "Verify.csproj")

    dotnet restore (Join-Path $ProjectDir "Verify.csproj") --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Restore failed" }

    dotnet build (Join-Path $ProjectDir "Verify.csproj") --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Verify project build failed" }

    $RefDir = Join-Path $ProjectDir "bin/Debug/net10.0"
    $ErrFile = Join-Path $TempDir "stderr.log"

    # Clean output directory
    $OutDir = Join-Path $RepoRoot "examples/out"
    if (Test-Path $OutDir) {
        Remove-Item $OutDir -Recurse -Force
    }

    # Grand totals
    $grandPassed = 0
    $grandFailed = 0
    $grandResults = @()

    # ==================================================================
    # Loop over combinations
    # ==================================================================
    foreach ($c in $Combos) {
        $comboName = $c.Name
        $useCachedStdlib = $c.CachedStdlib
        $useCachedZunit = $c.CachedZunit

        Write-Host ""
        Write-Host "========================================"
        Write-Host "=== Combination: $comboName ==="
        Write-Host "========================================"

        $TranspileDir = Join-Path $OutDir "$comboName/transpile"
        $CscDir = Join-Path $OutDir "$comboName/csc"
        $IlDir = Join-Path $OutDir "$comboName/il"
        New-Item -ItemType Directory -Path $TranspileDir -Force | Out-Null
        New-Item -ItemType Directory -Path $CscDir -Force | Out-Null
        New-Item -ItemType Directory -Path $IlDir -Force | Out-Null

        # C# transpile always uses source (PersistedAssemblyBuilder DLLs reference
        # System.Private.CoreLib which the C# compiler can't resolve)
        $CsStdlibArgs = @('--package-path', "$RepoRoot/packages/stdlib")
        $CsZunitArgs = @('--module-path', "$RepoRoot/packages/zunit/src")

        # IL backend respects cache flags
        $IlStdlibArgs = @()
        if ($useCachedStdlib) {
            # omit --package-path; compiler auto-loads from cache
        } else {
            $IlStdlibArgs = @('--package-path', "$RepoRoot/packages/stdlib")
        }

        $IlZunitArgs = @()
        if ($useCachedZunit) {
            $IlZunitArgs = @('--precompiled', (Join-Path $CacheRoot "zscript-zunit/0.1.0/zscript-zunit.dll"))
        } else {
            $IlZunitArgs = @('--module-path', "$RepoRoot/packages/zunit/src")
        }

        # Per-combo trackers
        $transpilePassed = 0
        $transpileFailed = 0
        $transpileFailures = @()
        $transpileSucceededNames = @()

        $cscPassed = 0
        $cscFailed = 0
        $cscFailures = @()

        $ilPassed = 0
        $ilFailed = 0
        $ilFailures = @()

        # ==============================================================
        # Phase 1: ZScript -> C# Transpile
        # ==============================================================
        Write-Host ""
        Write-Host "=== Phase 1: ZScript -> C# Transpile ==="

        foreach ($zsFile in Get-ChildItem "$RepoRoot/examples/*.zs") {
            $name = $zsFile.BaseName
            if ($Examples.Count -gt 0 -and $name -notin $Examples) { continue }
            Write-Host -NoNewline "  $name ... "

            $csOut = Join-Path $TranspileDir "$name.cs"
            $prevPref = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            dotnet run --no-build --project "$RepoRoot/src/ZScript.Cli" -- `
                compile $zsFile.FullName @CsStdlibArgs `
                @CsZunitArgs `
                --ref "$RefDir" `
                -o $csOut @DebugArgs 2>$ErrFile
            $ErrorActionPreference = $prevPref

            if ($LASTEXITCODE -ne 0) {
                Write-Host "FAIL"
                if (Test-Path $ErrFile) {
                    Get-Content $ErrFile | ForEach-Object { Write-Host "    $_" }
                }
                $transpileFailed++
                $transpileFailures += $name
                Remove-Item $csOut -ErrorAction SilentlyContinue
            } elseif (-not (Test-Path $csOut)) {
                Write-Host "FAIL (no .cs generated)"
                $transpileFailed++
                $transpileFailures += $name
            } else {
                Write-Host "OK"
                $transpilePassed++
                $transpileSucceededNames += $name
            }
        }

        $totalTranspile = $transpilePassed + $transpileFailed
        Write-Host "  $transpilePassed/$totalTranspile passed"

        # ==============================================================
        # Phase 2: C# Compile (csc)
        # ==============================================================
        Write-Host ""
        Write-Host "=== Phase 2: C# Compile (csc) ==="

        foreach ($name in $transpileSucceededNames) {
            Write-Host -NoNewline "  $name ... "

            Copy-Item (Join-Path $TranspileDir "$name.cs") (Join-Path $ProjectDir "Example.cs")

            $prevPref = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            dotnet build (Join-Path $ProjectDir "Verify.csproj") --no-restore --nologo -v quiet 2>$ErrFile
            $ErrorActionPreference = $prevPref

            if ($LASTEXITCODE -eq 0) {
                Write-Host "OK"
                Copy-Item (Join-Path $RefDir "Verify.dll") (Join-Path $CscDir "$name.dll")
                $cscPassed++
            } else {
                Write-Host "FAIL"
                if (Test-Path $ErrFile) {
                    Get-Content $ErrFile | ForEach-Object { Write-Host "    $_" }
                }
                $cscFailed++
                $cscFailures += $name
            }

            Remove-Item (Join-Path $ProjectDir "Example.cs") -ErrorAction SilentlyContinue
        }

        $totalCsc = $cscPassed + $cscFailed
        Write-Host "  $cscPassed/$totalCsc passed"

        # ==============================================================
        # Phase 3: ZScript -> IL Direct Compile
        # ==============================================================
        Write-Host ""
        Write-Host "=== Phase 3: ZScript -> IL Direct Compile ==="

        foreach ($zsFile in Get-ChildItem "$RepoRoot/examples/*.zs") {
            $name = $zsFile.BaseName
            if ($Examples.Count -gt 0 -and $name -notin $Examples) { continue }
            Write-Host -NoNewline "  $name ... "

            $ilOut = Join-Path $IlDir "$name.dll"
            $prevPref = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            dotnet run --no-build --project "$RepoRoot/src/ZScript.Cli" -- `
                compile $zsFile.FullName --backend il @IlStdlibArgs `
                @IlZunitArgs `
                --ref "$RefDir" `
                -o $ilOut @DebugArgs 2>$ErrFile
            $ErrorActionPreference = $prevPref

            if ($LASTEXITCODE -eq 0) {
                Write-Host "OK"
                $ilPassed++
            } else {
                Write-Host "FAIL"
                if (Test-Path $ErrFile) {
                    Get-Content $ErrFile | ForEach-Object { Write-Host "    $_" }
                }
                $ilFailed++
                $ilFailures += $name
            }
        }

        $totalIl = $ilPassed + $ilFailed
        Write-Host "  $ilPassed/$totalIl passed"

        # Per-combo summary
        Write-Host ""
        Write-Host "--- $comboName summary ---"
        Write-Host "  ZScript -> C# Transpile:  $transpilePassed/$totalTranspile passed"
        Write-Host "  C# Compile (csc):         $cscPassed/$totalCsc passed"
        Write-Host "  IL Direct Compile:         $ilPassed/$totalIl passed"

        $comboTotalFailed = $transpileFailed + $cscFailed + $ilFailed
        $comboTotalPassed = $transpilePassed + $cscPassed + $ilPassed
        $grandPassed += $comboTotalPassed
        $grandFailed += $comboTotalFailed

        if ($comboTotalFailed -gt 0) {
            $grandResults += "FAIL: $comboName ($comboTotalFailed failures)"
            if ($transpileFailures.Count -gt 0) {
                $grandResults += "       transpile: $($transpileFailures -join ', ')"
            }
            if ($cscFailures.Count -gt 0) {
                $grandResults += "       csc: $($cscFailures -join ', ')"
            }
            if ($ilFailures.Count -gt 0) {
                $grandResults += "       il: $($ilFailures -join ', ')"
            }
        } else {
            $grandResults += "PASS: $comboName"
        }
    }

    # ==================================================================
    # Grand Summary
    # ==================================================================
    Write-Host ""
    Write-Host "========================================"
    Write-Host "=== Grand Summary ==="
    Write-Host "========================================"
    foreach ($r in $grandResults) {
        Write-Host "  $r"
    }
    Write-Host ""
    Write-Host "  Total: $grandPassed passed, $grandFailed failed"

    if ($grandFailed -gt 0) {
        exit 1
    }
} finally {
    if ($TempDir -and (Test-Path $TempDir)) {
        Remove-Item $TempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
