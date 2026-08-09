#!/usr/bin/env pwsh
# Dot-source this file to get Format-ZsDiagnostics. It lives in its own file so a
# `ForEach-Object -Parallel` block can dot-source it: a parallel runspace inherits neither
# the caller's functions nor (by design) a script block passed through `$using:`.

function Format-ZsDiagnostics {
    <#
    .SYNOPSIS
        Summarizes a build/test log down to its distinct diagnostics.
    .DESCRIPTION
        MSBuild and `dotnet test` write their errors to stdout, so capturing stderr alone
        leaves a failure with nothing to show. Pulls the distinct `error CSxxxx`-style lines
        and failed-test lines out of the whole log; falls back to the log's tail when the
        failure had no recognizable diagnostic (a crash, a missing SDK).
    #>
    param(
        [Parameter(Mandatory)][string]$LogPath,
        [int]$Max = 25
    )

    if (-not (Test-Path $LogPath)) { return $null }

    $lines = @(Get-Content $LogPath |
        Select-String -Pattern '(error [A-Z]+\d+|^\s*Failed\s)' |
        ForEach-Object { $_.Line.Trim() } |
        Sort-Object -Unique)

    if ($lines.Count -eq 0) {
        return ((Get-Content $LogPath -Tail 20) -join "`n")
    }
    if ($lines.Count -gt $Max) {
        return (($lines | Select-Object -First $Max) -join "`n") + "`n    ... $($lines.Count - $Max) more"
    }
    return ($lines -join "`n")
}
