#!/usr/bin/env pwsh
param(
    [switch]$CachedStdlib,
    [switch]$CachedZunit
)

$ErrorActionPreference = 'Stop'

$RepoRoot = $PSScriptRoot
$TempDir = $null
$CacheRoot = Join-Path $env:LOCALAPPDATA "zscript\cache\pkg"

try {
    Write-Host "=== Building solution ==="
    dotnet build "$RepoRoot/ZScript.slnx" --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Solution build failed" }

    if ($CachedStdlib) {
        Write-Host "=== Packing stdlib ==="
        dotnet run --no-build --project "$RepoRoot/src/ZScript.Cli" -- `
            pack -m "$RepoRoot/packages/stdlib/package.zspkg"
        if ($LASTEXITCODE -ne 0) { throw "Packing stdlib failed" }
    }

    if ($CachedZunit) {
        Write-Host "=== Packing ZUnit ==="
        dotnet run --no-build --project "$RepoRoot/src/ZScript.Cli" -- `
            pack -m "$RepoRoot/packages/zunit/package.zspkg"
        if ($LASTEXITCODE -ne 0) { throw "Packing ZUnit failed" }
    }

    $TempDir = Join-Path ([System.IO.Path]::GetTempPath()) "zscript-verify-$([System.Guid]::NewGuid().ToString('N').Substring(0,8))"
    $ProjectDir = Join-Path $TempDir "verify"
    New-Item -ItemType Directory -Path $ProjectDir -Force | Out-Null

    $RuntimeCsproj = Join-Path $RepoRoot "src/ZScript.Runtime/ZScript.Runtime.csproj"

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
    <ProjectReference Include="$RuntimeCsproj" />
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
    $CsOutDir = Join-Path $TempDir "transpiled"
    New-Item -ItemType Directory -Path $CsOutDir -Force | Out-Null

    # Build compile args based on caching flags
    # C# backend always compiles stdlib from source (PersistedAssemblyBuilder DLLs
    # reference System.Private.CoreLib which the C# compiler can't resolve).
    # IL backend can use the precompiled cache directly.
    $CsStdlibArgs = @('--stdlib', "$RepoRoot/packages/stdlib/src")
    $IlStdlibArgs = @()
    if ($CachedStdlib) {
        # IL uses cache; C# still uses source (set above)
    } else {
        $IlStdlibArgs = @('--stdlib', "$RepoRoot/packages/stdlib/src")
    }

    # Same for ZUnit: C# needs source, IL can use precompiled
    $CsZunitArgs = @('--module-path', "$RepoRoot/packages/zunit/src")
    $IlZunitArgs = @()
    if ($CachedZunit) {
        $IlZunitArgs = @('--precompiled', (Join-Path $CacheRoot "zscript-zunit/0.1.0/zscript-zunit.dll"))
    } else {
        $IlZunitArgs = @('--module-path', "$RepoRoot/packages/zunit/src")
    }

    # Track results per phase
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

    # ======================================================================
    # Phase 1: ZScript -> C# Transpile
    # ======================================================================
    Write-Host ""
    Write-Host "=== Phase 1: ZScript -> C# Transpile ==="

    foreach ($zsFile in Get-ChildItem "$RepoRoot/examples/*.zs") {
        $name = $zsFile.BaseName
        Write-Host -NoNewline "  $name ... "

        $csOut = Join-Path $CsOutDir "$name.cs"
        $prevPref = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        dotnet run --no-build --project "$RepoRoot/src/ZScript.Cli" -- `
            compile $zsFile.FullName @CsStdlibArgs `
            @CsZunitArgs `
            --ref "$RefDir" `
            -o $csOut 2>$ErrFile
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

    # ======================================================================
    # Phase 2: C# Compile (csc)
    # ======================================================================
    Write-Host ""
    Write-Host "=== Phase 2: C# Compile (csc) ==="

    foreach ($name in $transpileSucceededNames) {
        Write-Host -NoNewline "  $name ... "

        Copy-Item (Join-Path $CsOutDir "$name.cs") (Join-Path $ProjectDir "Example.cs")

        $prevPref = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        dotnet build (Join-Path $ProjectDir "Verify.csproj") --no-restore --nologo -v quiet 2>$ErrFile
        $ErrorActionPreference = $prevPref

        if ($LASTEXITCODE -eq 0) {
            Write-Host "OK"
            Copy-Item (Join-Path $ProjectDir "Example.cs") (Join-Path $RepoRoot "examples/$name.cs")
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

    # ======================================================================
    # Phase 3: ZScript -> IL Direct Compile
    # ======================================================================
    Write-Host ""
    Write-Host "=== Phase 3: ZScript -> IL Direct Compile ==="

    foreach ($zsFile in Get-ChildItem "$RepoRoot/examples/*.zs") {
        $name = $zsFile.BaseName
        Write-Host -NoNewline "  $name ... "

        $ilOut = Join-Path $ProjectDir "$name.dll"
        $prevPref = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        dotnet run --no-build --project "$RepoRoot/src/ZScript.Cli" -- `
            compile $zsFile.FullName --backend il @IlStdlibArgs `
            @IlZunitArgs `
            --ref "$RefDir" `
            -o $ilOut 2>$ErrFile
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

        Remove-Item (Join-Path $ProjectDir "$name.dll") -ErrorAction SilentlyContinue
        Remove-Item (Join-Path $ProjectDir "$name.exe") -ErrorAction SilentlyContinue
    }

    $totalIl = $ilPassed + $ilFailed
    Write-Host "  $ilPassed/$totalIl passed"

    # ======================================================================
    # Summary
    # ======================================================================
    Write-Host ""
    Write-Host "=== Summary ==="
    Write-Host "  ZScript -> C# Transpile: $transpilePassed/$totalTranspile passed"
    Write-Host "  C# Compile (csc):        $cscPassed/$totalCsc passed"
    Write-Host "  IL Direct Compile:        $ilPassed/$totalIl passed"

    $hasFailures = $false
    if ($transpileFailures.Count -gt 0) {
        Write-Host ""
        Write-Host "Transpile failures: $($transpileFailures -join ', ')"
        $hasFailures = $true
    }
    if ($cscFailures.Count -gt 0) {
        Write-Host "C# compile failures: $($cscFailures -join ', ')"
        $hasFailures = $true
    }
    if ($ilFailures.Count -gt 0) {
        Write-Host "IL compile failures: $($ilFailures -join ', ')"
        $hasFailures = $true
    }
    if ($hasFailures) {
        exit 1
    }
} finally {
    if ($TempDir -and (Test-Path $TempDir)) {
        Remove-Item $TempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
