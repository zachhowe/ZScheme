#!/usr/bin/env pwsh
param(
    [switch]$AllowOldPowerShellVersionsAndRiskFailingScripts
)

if ($PSVersionTable.PSVersion -lt [Version]'7.6.0' -and -not $AllowOldPowerShellVersionsAndRiskFailingScripts) {
    Write-Error "This script requires PowerShell 7.6.0 or newer (pwsh). Current version: $($PSVersionTable.PSVersion). Pass -AllowOldPowerShellVersionsAndRiskFailingScripts to override."
    exit 1
}

$ErrorActionPreference = 'Stop'
# Check native command exit codes explicitly (mirrors the bash installer) rather
# than letting a non-zero exit throw before our friendly messages can print.
$PSNativeCommandUseErrorActionPreference = $false

$ScriptDir = $PSScriptRoot
$RepoRoot = Split-Path -Parent $ScriptDir
Set-Location $RepoRoot

# Ensure dotnet is available
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error "dotnet is not installed or not in PATH."
    exit 1
}

# Restore local tools (CSharpier) so the first commit doesn't pay for it
dotnet tool restore *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Error "'dotnet tool restore' failed; CSharpier is unavailable."
    exit 1
}

# Resolve the hooks directory; git-path may be relative (.git/hooks) or absolute (worktrees).
$HooksDir = (git rev-parse --git-path hooks).Trim()
if ($LASTEXITCODE -ne 0) {
    Write-Error "Not a git repository (or git is unavailable)."
    exit 1
}
$HooksDir = [System.IO.Path]::GetFullPath($HooksDir, $RepoRoot)

# Ensure the hooks directory exists
New-Item -ItemType Directory -Force -Path $HooksDir | Out-Null

# Copy pre-commit hook
$HookFile = Join-Path $HooksDir 'pre-commit'
Copy-Item -Path (Join-Path $ScriptDir 'hooks/pre-commit') -Destination $HookFile -Force

# On Unix the hook needs the executable bit; Git for Windows ignores it.
if (-not $IsWindows) {
    chmod +x $HookFile
}

Write-Host "Pre-commit hook installed at $HookFile"
