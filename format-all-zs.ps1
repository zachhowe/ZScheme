#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Reformat every .zs file in the repository with the ZScheme formatter.

.DESCRIPTION
    Builds the ZScheme CLI once, then runs `zs format <file> --write` over every
    .zs source file in the repo (generated bin/obj/out trees are skipped).

    Files the formatter declines to rewrite (lexer errors, or the re-lex safety
    guard tripping) are reported as warnings and left untouched; they do not fail
    the run unless -CI is passed.

.PARAMETER Paths
    Optional list of files or directories to restrict formatting to. Defaults to
    the whole repository.

.PARAMETER Check
    Dry run: report which files WOULD change without writing them. Exits non-zero
    if any file is not already formatted (useful in CI).

.PARAMETER CI
    Treat formatter warnings (declined files) as failures (non-zero exit).

.EXAMPLE
    pwsh ./format-all-zs.ps1

.EXAMPLE
    pwsh ./format-all-zs.ps1 -Check

.EXAMPLE
    pwsh ./format-all-zs.ps1 -Paths packages/stdlib,examples/factorial.zs
#>
param(
    [string[]]$Paths = @(),
    [switch]$Check,
    [switch]$CI,
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
$CliProject = Join-Path $RepoRoot 'src/ZScheme.Cli'
$Write = -not $Check
$TempDir = $null

try {
    # ==================================================================
    # Build the CLI once so the per-file --no-build runs are fast.
    # ==================================================================
    Write-Host "=== Building ZScheme CLI ==="
    dotnet build $CliProject --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "CLI build failed" }

    # ==================================================================
    # Discover .zs files. Skip generated trees (bin/obj/out) which may
    # contain copied/precompiled sources we should not rewrite.
    # ==================================================================
    $searchRoots = if ($Paths.Count -gt 0) {
        $Paths | ForEach-Object {
            if ([System.IO.Path]::IsPathRooted($_)) { $_ } else { Join-Path $RepoRoot $_ }
        }
    } else {
        @($RepoRoot)
    }

    $files = [System.Collections.Generic.List[string]]::new()
    foreach ($root in $searchRoots) {
        if (Test-Path -PathType Leaf $root) {
            if ($root -like '*.zs') { $files.Add((Resolve-Path $root).Path) }
            continue
        }
        if (-not (Test-Path -PathType Container $root)) {
            Write-Warning "Path not found, skipping: $root"
            continue
        }
        Get-ChildItem -Path $root -Recurse -File -Filter *.zs |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj|out)[\\/]' } |
            ForEach-Object { $files.Add($_.FullName) }
    }

    $allFiles = @($files | Sort-Object -Unique)

    Write-Host ""
    Write-Host "=== Formatting $($allFiles.Count) .zs file(s) (ThrottleLimit=$ThrottleLimit$(if ($Check) { ', check-only' })) ==="

    if ($allFiles.Count -eq 0) {
        Write-Host "  (no .zs files found)"
        exit 0
    }

    $TempDir = Join-Path ([System.IO.Path]::GetTempPath()) "zscheme-format-$([System.Guid]::NewGuid().ToString('N').Substring(0,8))"
    New-Item -ItemType Directory -Path $TempDir -Force | Out-Null

    $results = @($allFiles | ForEach-Object -Parallel {
        $file = $_
        $name = [System.IO.Path]::GetFileName($file)
        $safeName = ($file -replace '[^A-Za-z0-9]', '_')
        $errFile = Join-Path $using:TempDir "stderr-$safeName.log"

        $fmtArgs = @('format', $file)
        if ($using:Write) { $fmtArgs += '--write' }

        $ErrorActionPreference = 'Continue'
        $stdout = dotnet run --no-build --project $using:CliProject -- @fmtArgs 2>$errFile
        $exitCode = $LASTEXITCODE

        $errText = if (Test-Path $errFile) { ((Get-Content $errFile -Raw) ?? '').Trim() } else { '' }

        # Classify the outcome.
        #   --write: stdout is "Formatted: <path>" or "No changes: <path>".
        #   --check (no --write): stdout is the formatted source; compare to original.
        #   Non-zero exit means the formatter declined (warning on stderr) or errored.
        if ($exitCode -ne 0) {
            $status = if ($errText) { 'Warning' } else { 'Error' }
            [PSCustomObject]@{ Name = $name; Path = $file; Status = $status; Message = $errText }
        }
        elseif ($using:Write) {
            $joined = ($stdout -join "`n")
            if ($joined -match '^\s*Formatted:') {
                [PSCustomObject]@{ Name = $name; Path = $file; Status = 'Formatted'; Message = $null }
            } else {
                [PSCustomObject]@{ Name = $name; Path = $file; Status = 'Unchanged'; Message = $null }
            }
        }
        else {
            # Check mode: did formatting produce different bytes than the original?
            $original = Get-Content -Raw -LiteralPath $file
            $formatted = ($stdout -join "`n")
            # `dotnet run` strips a trailing newline from captured stdout; normalize line endings for comparison.
            $a = ($original -replace "`r`n", "`n").TrimEnd("`n")
            $b = ($formatted -replace "`r`n", "`n").TrimEnd("`n")
            if ($a -ne $b) {
                [PSCustomObject]@{ Name = $name; Path = $file; Status = 'WouldChange'; Message = $null }
            } else {
                [PSCustomObject]@{ Name = $name; Path = $file; Status = 'Unchanged'; Message = $null }
            }
        }
    } -ThrottleLimit $ThrottleLimit)

    # ==================================================================
    # Report
    # ==================================================================
    $changed = @($results | Where-Object { $_.Status -in @('Formatted', 'WouldChange') })
    $unchanged = @($results | Where-Object { $_.Status -eq 'Unchanged' })
    $warnings = @($results | Where-Object { $_.Status -eq 'Warning' })
    $errors = @($results | Where-Object { $_.Status -eq 'Error' })

    Write-Host ""
    foreach ($r in ($changed | Sort-Object Path)) {
        $verb = if ($Check) { 'would format' } else { 'formatted' }
        Write-Host "  $verb : $($r.Path)"
    }
    foreach ($r in (($warnings + $errors) | Sort-Object Path)) {
        Write-Host "  SKIPPED   : $($r.Path)" -ForegroundColor Yellow
        if ($r.Message) {
            $r.Message -split "`n" | ForEach-Object { Write-Host "      $_" -ForegroundColor Yellow }
        }
    }

    Write-Host ""
    Write-Host "========================================"
    Write-Host "=== Summary ==="
    Write-Host "========================================"
    Write-Host "  $(if ($Check) { 'Would change' } else { 'Formatted   ' }): $($changed.Count)"
    Write-Host "  Unchanged    : $($unchanged.Count)"
    Write-Host "  Skipped (warn): $($warnings.Count)"
    Write-Host "  Errors        : $($errors.Count)"
    Write-Host "  Total          : $($results.Count)"

    # Exit codes:
    #   - Errors always fail.
    #   - -CI makes formatter warnings (declined files) fail too.
    #   - -Check fails if anything would change (enforce "already formatted").
    if ($errors.Count -gt 0) { exit 1 }
    if ($CI -and $warnings.Count -gt 0) { exit 1 }
    if ($Check -and $changed.Count -gt 0) { exit 1 }
    exit 0
}
finally {
    if ($TempDir -and (Test-Path $TempDir)) {
        Remove-Item $TempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
