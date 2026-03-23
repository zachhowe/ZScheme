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

    $csPassed = 0
    $csFailed = 0
    $csFailures = @()
    $ilPassed = 0
    $ilFailed = 0
    $ilFailures = @()

    $RefDir = Join-Path $ProjectDir "bin/Debug/net10.0"

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

    foreach ($zsFile in Get-ChildItem "$RepoRoot/examples/*.zs") {
        $name = $zsFile.BaseName

        # --- C# backend ---
        Write-Host -NoNewline "  $name (C#) ... "

        $csOut = Join-Path $ProjectDir "$name.cs"
        $prevPref = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        dotnet run --no-build --project "$RepoRoot/src/ZScript.Cli" -- `
            compile $zsFile.FullName @CsStdlibArgs `
            @CsZunitArgs `
            --ref "$RefDir" `
            -o $csOut 2>$null
        $ErrorActionPreference = $prevPref
        if ($LASTEXITCODE -ne 0) {
            Write-Host "FAIL (zs compile)"
            $csFailed++
            $csFailures += $name
            Remove-Item $csOut -ErrorAction SilentlyContinue
        } elseif (-not (Test-Path $csOut)) {
            Write-Host "FAIL (no .cs generated)"
            $csFailed++
            $csFailures += $name
        } else {
            Rename-Item $csOut (Join-Path $ProjectDir "Example.cs")

            dotnet build (Join-Path $ProjectDir "Verify.csproj") --no-restore --nologo -v quiet 2>$null
            if ($LASTEXITCODE -eq 0) {
                Write-Host "OK"
                Copy-Item (Join-Path $ProjectDir "Example.cs") (Join-Path $RepoRoot "examples/$name.cs")
                $csPassed++
            } else {
                Write-Host "FAIL (csc)"
                $csFailed++
                $csFailures += $name
            }

            Remove-Item (Join-Path $ProjectDir "Example.cs") -ErrorAction SilentlyContinue
        }

        # --- IL backend ---
        Write-Host -NoNewline "  $name (IL) ... "

        $ilOut = Join-Path $ProjectDir "$name.dll"
        $prevPref = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        dotnet run --no-build --project "$RepoRoot/src/ZScript.Cli" -- `
            compile $zsFile.FullName --backend il @IlStdlibArgs `
            @IlZunitArgs `
            --ref "$RefDir" `
            -o $ilOut 2>$null
        $ErrorActionPreference = $prevPref
        if ($LASTEXITCODE -eq 0) {
            Write-Host "OK"
            $ilPassed++
        } else {
            Write-Host "FAIL (il compile)"
            $ilFailed++
            $ilFailures += $name
        }

        Remove-Item (Join-Path $ProjectDir "$name.dll") -ErrorAction SilentlyContinue
        Remove-Item (Join-Path $ProjectDir "$name.exe") -ErrorAction SilentlyContinue
    }

    $totalCs = $csPassed + $csFailed
    $totalIl = $ilPassed + $ilFailed
    Write-Host ""
    Write-Host "=== Results: $csPassed/$totalCs C# passed, $ilPassed/$totalIl IL passed ==="
    if ($csFailures.Count -gt 0) {
        Write-Host "C# failures: $($csFailures -join ', ')"
    }
    if ($ilFailures.Count -gt 0) {
        Write-Host "IL failures: $($ilFailures -join ', ')"
    }
    if ($csFailures.Count -gt 0 -or $ilFailures.Count -gt 0) {
        exit 1
    }
} finally {
    if ($TempDir -and (Test-Path $TempDir)) {
        Remove-Item $TempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
