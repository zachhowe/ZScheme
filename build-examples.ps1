#!/usr/bin/env pwsh
$ErrorActionPreference = 'Stop'

$RepoRoot = $PSScriptRoot
$TempDir = $null

try {
    Write-Host "=== Building solution ==="
    dotnet build "$RepoRoot/ZScript.slnx" --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Solution build failed" }

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
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$RuntimeCsproj" />
  </ItemGroup>
</Project>
"@ | Set-Content (Join-Path $ProjectDir "Verify.csproj")

    dotnet restore (Join-Path $ProjectDir "Verify.csproj") --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Restore failed" }

    $passed = 0
    $failed = 0
    $failures = @()

    foreach ($zsFile in Get-ChildItem "$RepoRoot/examples/*.zs") {
        $name = $zsFile.BaseName
        Write-Host -NoNewline "  $name ... "

        # Compile .zs -> .cs
        $csOut = Join-Path $ProjectDir "$name.cs"
        dotnet run --no-build --project "$RepoRoot/src/ZScript.Cli" -- `
            compile $zsFile.FullName --stdlib "$RepoRoot/src/ZScript.StdLib" `
            -o $csOut 2>$null
        if ($LASTEXITCODE -ne 0) {
            Write-Host "FAIL (zs compile)"
            $failed++
            $failures += $name
            Remove-Item $csOut -ErrorAction SilentlyContinue
            continue
        }

        if (-not (Test-Path $csOut)) {
            Write-Host "FAIL (no .cs generated)"
            $failed++
            $failures += $name
            continue
        }

        Rename-Item $csOut (Join-Path $ProjectDir "Example.cs")

        # Verify C# compiles
        dotnet build (Join-Path $ProjectDir "Verify.csproj") --no-restore --nologo -v quiet 2>$null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "OK"
            $passed++
        } else {
            Write-Host "FAIL (csc)"
            $failed++
            $failures += $name
        }

        Remove-Item (Join-Path $ProjectDir "Example.cs") -ErrorAction SilentlyContinue
    }

    Write-Host ""
    Write-Host "=== Results: $passed passed, $failed failed ==="
    if ($failures.Count -gt 0) {
        Write-Host "Failures: $($failures -join ', ')"
        exit 1
    }
} finally {
    if ($TempDir -and (Test-Path $TempDir)) {
        Remove-Item $TempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
