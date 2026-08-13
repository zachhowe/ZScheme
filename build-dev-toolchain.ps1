#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds zs and zs-lsp into one directory and registers it with zsup as a linked toolchain.

.DESCRIPTION
    The CLI and the language server are separate projects with separate output directories, so
    neither project's bin/<config>/net10.0 is a complete toolchain on its own: linking the CLI's
    gives a `zs` that works and an editor that cannot start a language server, and linking the
    language server's gives a tree zsup cannot even find `zs` in. This assembles the same layout
    publish.ps1 produces for a release -- both executables in one bin/ -- and links that.

    The link is a pointer, not a copy, so once it exists every subsequent build of either project
    into the same directory is live with no reinstall step. Rerun this script to rebuild; rerun it
    with -Use only when you also want to switch the default toolchain.

    The output directory stays inside the checkout on purpose. The compiler finds the standard
    library by scanning up from its own location for a packages/ directory, so a dev tree under
    dist/ resolves to the repository's packages/ -- edits to stdlib sources are live too, and
    nothing has to be copied.

.EXAMPLE
    pwsh ./build-dev-toolchain.ps1
    pwsh ./build-dev-toolchain.ps1 -Use
    pwsh ./build-dev-toolchain.ps1 -Name pr-482 -Configuration Release
#>
param(
    [string]$Name = "dev",
    [string]$Configuration = "Debug",
    [string]$OutputDir,
    [switch]$Use,
    [switch]$NoLink,
    [switch]$AllowOldPowerShellVersionsAndRiskFailingScripts
)

if ($PSVersionTable.PSVersion -lt [Version]'7.6.0' -and -not $AllowOldPowerShellVersionsAndRiskFailingScripts) {
    Write-Error "This script requires PowerShell 7.6.0 or newer (pwsh). Current version: $($PSVersionTable.PSVersion). Pass -AllowOldPowerShellVersionsAndRiskFailingScripts to override."
    exit 1
}

$ErrorActionPreference = 'Stop'

$RepoRoot = $PSScriptRoot
$CliProject = "$RepoRoot/src/ZScheme.Cli/ZScheme.Cli.csproj"
$LspProject = "$RepoRoot/src/ZScheme.LanguageServer/ZScheme.LanguageServer.csproj"

if (-not $OutputDir) {
    $OutputDir = Join-Path $RepoRoot "dist/toolchain-$Name"
}

# bin/ under the toolchain root, matching an installed toolchain. zsup accepts a tree with the
# executables at the root as well, but keeping the two shapes identical means a dev toolchain
# exercises the same layout a release does.
$binDir = Join-Path $OutputDir "bin"
New-Item -ItemType Directory -Path $binDir -Force | Out-Null
$OutputDir = (Resolve-Path $OutputDir).Path
$binDir = (Resolve-Path $binDir).Path

Write-Host "Building the '$Name' toolchain ($Configuration)"
Write-Host "  -> $binDir"
Write-Host ""

foreach ($project in @($CliProject, $LspProject)) {
    $projectName = [System.IO.Path]::GetFileNameWithoutExtension($project)
    Write-Host "=== $projectName ==="

    dotnet build $project `
        --configuration $Configuration `
        --nologo `
        --output $binDir

    if ($LASTEXITCODE -ne 0) {
        # The overwhelmingly common cause on Windows, and one the raw MSBuild error does not
        # explain: an editor is holding zs-lsp.exe from this very directory open, so the build
        # cannot replace it. The file is locked for as long as the language server runs.
        if ($IsWindows) {
            Write-Host ""
            Write-Warning "If this failed because a file is in use, stop the language server in your editor (or close it) and rerun -- a running zs-lsp.exe locks its own binary."
        }
        throw "$projectName build failed"
    }

    Write-Host ""
}

foreach ($exe in @('zs', 'zs-lsp')) {
    $path = Join-Path $binDir ($IsWindows ? "$exe.exe" : $exe)
    if (-not (Test-Path $path)) {
        throw "Expected $path after the build but it is not there"
    }
}

if ($NoLink) {
    Write-Host "Built. Link it with: zsup link $Name $OutputDir"
    exit 0
}

# Not a hard failure: the tree is built and usable, and someone working on zsup itself may well
# have no installed zsup on PATH yet. Printing the command keeps the script useful either way.
if (-not (Get-Command zsup -ErrorAction SilentlyContinue)) {
    Write-Warning "zsup is not on PATH, so the toolchain was not linked."
    Write-Host "Run this once zsup is installed: zsup link $Name $OutputDir"
    exit 0
}

# --force has no equivalent here: `zsup link` overwrites its own .link file, and the only case it
# refuses is a name already taken by an *installed* toolchain, which silently replacing would be
# the wrong call.
zsup link $Name $OutputDir
if ($LASTEXITCODE -ne 0) { throw "zsup link failed" }

if ($Use) {
    zsup use $Name
    if ($LASTEXITCODE -ne 0) { throw "zsup use failed" }
}

Write-Host ""
Write-Host "Done. Rerun this script after a change; restart the language server in your editor to pick up a new zs-lsp."
