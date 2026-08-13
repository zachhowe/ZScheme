#!/usr/bin/env pwsh
<#
.SYNOPSIS
    End-to-end verification of the zsup toolchain manager against locally built artifacts.
.DESCRIPTION
    Installs zsup and a toolchain from dist/, then exercises the things unit tests cannot: the
    shim's process handoff, toolchain switching, per-project pinning, and -- most importantly --
    that a freshly installed toolchain can compile a program importing the standard library.

    Requires publish.ps1 and publish-zsup.ps1 to have run for $Rid. Set ZSCHEME_HOME to a scratch
    directory before invoking: this script DELETES it recursively, and refuses to run unless the
    path is under the repository, the system temp directory, or RUNNER_TEMP.
#>
param(
    [Parameter(Mandatory)]
    [string]$Rid,

    [string]$DistDir = "./dist",
    [switch]$AllowOldPowerShellVersionsAndRiskFailingScripts
)

if ($PSVersionTable.PSVersion -lt [Version]'7.6.0' -and -not $AllowOldPowerShellVersionsAndRiskFailingScripts) {
    Write-Error "This script requires PowerShell 7.6.0 or newer (pwsh). Current version: $($PSVersionTable.PSVersion). Pass -AllowOldPowerShellVersionsAndRiskFailingScripts to override."
    exit 1
}

$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$DistDir = (Resolve-Path (Join-Path $RepoRoot $DistDir)).Path

if (-not $env:ZSCHEME_HOME) {
    Write-Error "Set ZSCHEME_HOME to an isolated directory before running this script."
    exit 1
}

# This script deletes ZSCHEME_HOME recursively, and ZSCHEME_HOME is a variable real users export
# to point at their real toolchains. Refuse anything that is not clearly scratch space, so a
# developer who has it set in their profile does not lose every installed toolchain -- or, if it
# happens to be $HOME, a great deal more.
$ZsHome = [System.IO.Path]::GetFullPath($env:ZSCHEME_HOME)
$scratchRoots = @(
    [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()),
    [System.IO.Path]::GetFullPath($RepoRoot)
) + @($env:RUNNER_TEMP, $env:GITHUB_WORKSPACE |
    Where-Object { $_ } |
    ForEach-Object { [System.IO.Path]::GetFullPath($_) })

$comparison = if ($IsWindows) { 'OrdinalIgnoreCase' } else { 'Ordinal' }
$underScratch = $false
foreach ($root in $scratchRoots) {
    $prefix = [System.IO.Path]::TrimEndingDirectorySeparator($root) + [System.IO.Path]::DirectorySeparatorChar
    if ($ZsHome.StartsWith($prefix, $comparison)) { $underScratch = $true }
}

if (-not $underScratch) {
    Write-Error @"
Refusing to run: ZSCHEME_HOME is '$ZsHome', which is not scratch space.
This script deletes ZSCHEME_HOME recursively. Point it somewhere under the repo,
the system temp directory, or RUNNER_TEMP.
"@
    exit 1
}

if (Test-Path $ZsHome) { Remove-Item -Recurse -Force $ZsHome }
New-Item -ItemType Directory -Path $ZsHome -Force | Out-Null
$ZsHome = (Resolve-Path $ZsHome).Path
$env:ZSCHEME_HOME = $ZsHome

[xml]$buildProps = Get-Content "$RepoRoot/Directory.Build.props"
$version = ($buildProps.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ }).Trim()

$targetsWindows = $Rid -like 'win-*'
$exe = if ($targetsWindows) { '.exe' } else { '' }
$archiveExt = if ($targetsWindows) { 'zip' } else { 'tar.gz' }

$failures = @()
function Check {
    param([string]$Label, [scriptblock]$Action)
    Write-Host ""
    Write-Host "--- $Label"
    try {
        & $Action
        Write-Host "    PASS"
    } catch {
        Write-Host "    FAIL: $_"
        $script:failures += $Label
    }
}

function Expand-Any {
    param([string]$Archive, [string]$Dest)
    New-Item -ItemType Directory -Path $Dest -Force | Out-Null
    if ($Archive -like '*.zip') {
        Expand-Archive -Path $Archive -DestinationPath $Dest -Force
    } else {
        tar -xzf $Archive -C $Dest
    }
}

# === Install zsup itself ====================================================================
$binDir = Join-Path $ZsHome 'bin'
Expand-Any (Join-Path $DistDir "zsup-$version-$Rid.$archiveExt") $binDir
$zsup = Join-Path $binDir "zsup$exe"
if (-not $targetsWindows) { chmod +x $zsup }

$toolchainArchive = Join-Path $DistDir "zscheme-$version-$Rid.$archiveExt"

Write-Host "=== zsup end-to-end ($Rid, version $version) ==="
Write-Host "ZSCHEME_HOME: $ZsHome"

Check "install the toolchain from a local archive" {
    & $zsup install $version --from $toolchainArchive
    if ($LASTEXITCODE -ne 0) { throw "install exited $LASTEXITCODE" }
}

# The shims are stamped by install; from here on everything goes through them, which is what a
# real user's PATH would hit.
$zs = Join-Path $binDir "zs$exe"
$zsLsp = Join-Path $binDir "zs-lsp$exe"

Check "the shims exist" {
    if (-not (Test-Path $zs)) { throw "no zs shim at $zs" }
    if (-not (Test-Path $zsLsp)) { throw "no zs-lsp shim at $zsLsp" }
}

Check "zs --version reports the installed compiler" {
    $output = & $zs --version
    if ($LASTEXITCODE -ne 0) { throw "zs --version exited $LASTEXITCODE" }
    if ($output -notmatch [regex]::Escape($version)) { throw "expected $version in '$output'" }
}

# === The reason this feature needed a stdlib story ==========================================
# Compiled from a scratch directory with no packages/ anywhere above it, which is exactly the
# case that fails without the shipped package cache and the base-directory fallback.
Check "compile a program that imports stdlib, from outside any checkout" {
    $work = Join-Path ([System.IO.Path]::GetTempPath()) "zs-e2e-$([guid]::NewGuid().ToString('N').Substring(0,8))"
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    try {
        Copy-Item "$RepoRoot/examples/collections.zs" (Join-Path $work 'program.zs')
        Push-Location $work
        try {
            $output = & $zs compile program.zs -o out -b il 2>&1 | Out-String
            if ($LASTEXITCODE -ne 0) { throw "compile exited $LASTEXITCODE`n$output" }
            if ($output -match 'not installed and could not be auto-installed') {
                throw "the standard library was not available to the installed toolchain`n$output"
            }
            if (-not (Test-Path (Join-Path $work 'out.dll'))) { throw "no out.dll was produced`n$output" }
        } finally { Pop-Location }
    } finally {
        Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
    }
}

Check "install a second toolchain and switch between them" {
    & $zsup install "0.0.1-e2e" --from $toolchainArchive --no-default
    if ($LASTEXITCODE -ne 0) { throw "second install exited $LASTEXITCODE" }

    # Installed under a name that is not its compiler version, so the prebuilt cache must still
    # land under the version the compiler actually reads.
    $seeded = Join-Path $ZsHome "cache/pkg/$version"
    if (-not (Test-Path $seeded)) { throw "expected a seeded package cache at $seeded" }
    if (Test-Path (Join-Path $ZsHome 'cache/pkg/0.0.1-e2e')) {
        throw "the package cache was keyed by install name instead of compiler version"
    }

    & $zsup use "0.0.1-e2e"
    $resolved = & $zsup which zs 2>$null
    if ($resolved -notmatch '0\.0\.1-e2e') { throw "expected the second toolchain, got '$resolved'" }

    & $zsup use $version
    $resolved = & $zsup which zs 2>$null
    if ($resolved -notmatch [regex]::Escape($version)) { throw "expected $version, got '$resolved'" }
}

Check "a .zscheme-version pin applies in its own tree only" {
    $work = Join-Path ([System.IO.Path]::GetTempPath()) "zs-pin-$([guid]::NewGuid().ToString('N').Substring(0,8))"
    $nested = Join-Path $work 'a/b/c'
    New-Item -ItemType Directory -Path $nested -Force | Out-Null
    try {
        Set-Content -Path (Join-Path $work '.zscheme-version') -Value '0.0.1-e2e'

        Push-Location $nested
        try {
            $pinned = & $zsup which zs 2>$null
            if ($pinned -notmatch '0\.0\.1-e2e') { throw "pin did not apply in a nested dir, got '$pinned'" }
        } finally { Pop-Location }

        Push-Location ([System.IO.Path]::GetTempPath())
        try {
            $unpinned = & $zsup which zs 2>$null
            if ($unpinned -match '0\.0\.1-e2e') { throw "pin leaked outside its tree" }
        } finally { Pop-Location }
    } finally {
        Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
    }
}

Check "ZSCHEME_VERSION overrides the default" {
    $env:ZSCHEME_VERSION = '0.0.1-e2e'
    try {
        $resolved = & $zsup which zs 2>$null
        if ($resolved -notmatch '0\.0\.1-e2e') { throw "env var did not win, got '$resolved'" }
    } finally {
        Remove-Item Env:\ZSCHEME_VERSION
    }
}

# A toolchain installed under a non-version name has to be able to compile too -- that is the
# case where a cache keyed by install name silently forces a from-source stdlib build.
Check "a toolchain installed under a different name can compile stdlib" {
    $work = Join-Path ([System.IO.Path]::GetTempPath()) "zs-alt-$([guid]::NewGuid().ToString('N').Substring(0,8))"
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    try {
        Copy-Item "$RepoRoot/examples/collections.zs" (Join-Path $work 'program.zs')
        Push-Location $work
        try {
            $env:ZSCHEME_VERSION = '0.0.1-e2e'
            try {
                $output = & $zs compile program.zs -o out -b il 2>&1 | Out-String
                if ($LASTEXITCODE -ne 0) { throw "compile exited $LASTEXITCODE`n$output" }
                if ($output -match 'not installed and could not be auto-installed') {
                    throw "stdlib was not available to the renamed toolchain`n$output"
                }
            } finally { Remove-Item Env:\ZSCHEME_VERSION }
        } finally { Pop-Location }
    } finally {
        Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
    }
}

Check "the shim forwards a non-zero exit code" {
    & $zs --definitely-not-a-command 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) { throw "expected a non-zero exit code" }
}

# === The stdio check ========================================================================
# The single automated detector of a redirected-handle mistake in the Windows shim: if the shim
# interposed a pipe, the framing would be corrupted and no valid response would come back.
Check "zs-lsp speaks LSP through the shim" {
    $body = '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"processId":null,"rootUri":null,"capabilities":{}}}'
    $request = "Content-Length: $($body.Length)`r`n`r`n$body"

    $psi = [System.Diagnostics.ProcessStartInfo]::new($zsLsp)
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.UseShellExecute = $false
    $process = [System.Diagnostics.Process]::Start($psi)
    try {
        $process.StandardInput.Write($request)
        $process.StandardInput.Flush()

        # A stream read returns what is available, not what was asked for, so the header and the
        # body can land in separate reads. Accumulate until both assertions below can be answered,
        # against one 60s budget rather than a fresh one per read -- a single read that returns
        # only 'Content-Length: N\r\n\r\n' would otherwise fail the "result" assertion and blame
        # the shim for a working build.
        $buffer = [char[]]::new(4096)
        $response = [System.Text.StringBuilder]::new()
        $clock = [System.Diagnostics.Stopwatch]::StartNew()
        $budget = [timespan]::FromSeconds(60)
        $complete = $false
        $eof = $false

        while (-not $complete -and -not $eof) {
            $remaining = $budget - $clock.Elapsed
            if ($remaining -le [timespan]::Zero) { break }

            $read = $process.StandardOutput.ReadAsync($buffer, 0, $buffer.Length)
            if (-not $read.Wait($remaining)) { break }
            if ($read.Result -le 0) { $eof = $true; break }

            [void]$response.Append($buffer, 0, $read.Result)
            $text = $response.ToString()
            $complete = ($text -match 'Content-Length:') -and ($text -match '"result"')
        }

        $text = $response.ToString()
        if (-not $complete -and -not $eof) {
            if ($text.Length -eq 0) { throw "no response within 60s" }
            throw "an incomplete response within 60s: '$text'"
        }
        if ($text -notmatch 'Content-Length:') { throw "no LSP framing in the response: '$text'" }
        if ($text -notmatch '"result"') { throw "no result in the response: '$text'" }
    } finally {
        if (-not $process.HasExited) { $process.Kill($true) }
        $process.Dispose()
    }
}

if (-not $targetsWindows) {
    Check "a hand-extracted release has an executable zs" {
        $work = Join-Path ([System.IO.Path]::GetTempPath()) "zs-tar-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory -Path $work -Force | Out-Null
        try {
            tar -xzf $toolchainArchive -C $work
            $extracted = Join-Path $work 'bin/zs'
            if (-not (Test-Path $extracted)) { throw "no bin/zs in the archive" }
            $mode = [System.IO.File]::GetUnixFileMode($extracted)
            if (-not ($mode -band [System.IO.UnixFileMode]::UserExecute)) {
                throw "bin/zs is not executable; the tarball was probably built on Windows"
            }
        } finally {
            Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
        }
    }
}

# --purge-cache rather than a bare uninstall: both toolchains here were installed from the same
# archive, so they share cache/pkg/<version>. Purging it because one of them went away would force
# the survivor into a from-source stdlib build, which needs the SDK and the network.
Check "uninstall removes a toolchain and keeps a cache another one shares" {
    $shared = Join-Path $ZsHome "cache/pkg/$version"
    & $zsup uninstall "0.0.1-e2e" --purge-cache | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "uninstall exited $LASTEXITCODE" }
    $listed = & $zsup list | Out-String
    if ($listed -match '0\.0\.1-e2e') { throw "the toolchain is still listed" }
    if (-not (Test-Path $shared)) { throw "the package cache $version still depends on was purged" }
}

# A link file zsup cannot parse still makes the name a link, and a link never writes to
# cache/pkg/<name> -- so if a released toolchain of that name was ever installed, that directory is
# its cache and purging it costs an SDK-and-network rebuild. Written by hand because zsup does not
# produce a malformed link, which is the whole reason the parse can fail here.
Check "uninstall keeps the shared cache for a link whose file cannot be parsed" {
    $link = Join-Path $ZsHome "toolchains/e2e-badlink.link"
    $cache = Join-Path $ZsHome "cache/pkg/e2e-badlink"
    Set-Content -Path $link -Value '# comment-only: no target on any line'
    New-Item -ItemType Directory -Path $cache -Force | Out-Null
    Set-Content -Path (Join-Path $cache 'marker.txt') -Value 'released payload cache'

    & $zsup uninstall "e2e-badlink" --purge-cache | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "uninstall exited $LASTEXITCODE" }
    if (Test-Path $link) { throw "the link file was left behind" }
    if (-not (Test-Path $cache)) { throw "the package cache was purged for a linked toolchain" }

    Remove-Item -Recurse -Force $cache -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "================================================================"
if ($failures.Count -gt 0) {
    Write-Host "FAILED: $($failures.Count) check(s)"
    foreach ($f in $failures) { Write-Host "  - $f" }
    exit 1
}
Write-Host "All checks passed."
