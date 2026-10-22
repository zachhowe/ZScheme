#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds zsup from source and installs it into your ZScheme home.

.DESCRIPTION
    Nothing else in the repository turns this checkout into an installed zsup. The two release
    installers (install.ps1, install.sh) download a binary, and publish-zsup.ps1 builds from
    source but stops at packaging an archive in dist/ -- so someone working on zsup itself had no
    bootstrap short of hand-copying files into ~/.zscheme/bin. This closes the loop: publish the
    Native AOT binary, lay it down in $ZSCHEME_HOME/bin with the zs / zs-lsp shims beside it --
    the same layout a release install produces, hardlinks on Unix and copies on Windows -- and
    put bin on PATH unless -NoModifyPath.

    This installs zsup only, no toolchain. The usual next step for compiler work is
    pwsh ./build-dev-toolchain.ps1 -Use, which needs the zsup this script provides.

    AOT cannot cross-compile, so the binary is built for the machine running this script. Pass
    -Rid to publish for another machine you can build on the same way.

.EXAMPLE
    pwsh ./install-zsup.ps1
    pwsh ./install-zsup.ps1 -Configuration Debug
    pwsh ./install-zsup.ps1 -Rid linux-x64 -NoModifyPath
#>
param(
    [string]$Configuration = "Release",
    [string]$Rid,
    [switch]$NoModifyPath,
    [switch]$AllowOldPowerShellVersionsAndRiskFailingScripts
)

if ($PSVersionTable.PSVersion -lt [Version]'7.6.0' -and -not $AllowOldPowerShellVersionsAndRiskFailingScripts) {
    Write-Error "This script requires PowerShell 7.6.0 or newer (pwsh). Current version: $($PSVersionTable.PSVersion). Pass -AllowOldPowerShellVersionsAndRiskFailingScripts to override."
    exit 1
}

$ErrorActionPreference = 'Stop'

$RepoRoot = $PSScriptRoot
$Project = "$RepoRoot/src/ZScheme.Zsup/ZScheme.Zsup.csproj"

[xml]$buildProps = Get-Content "$RepoRoot/Directory.Build.props"
$version = ($buildProps.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ }).Trim()

# Same home resolution as the release installers: ZSCHEME_HOME wins, ~/.zscheme is the default.
# The shims resolve their home from that variable or from ~/.zscheme and never from their own
# location, so a custom home has to be persisted alongside PATH below -- PATH alone would leave
# the next shell's `zs` looking in an empty ~/.zscheme.
$defaultHome = Join-Path $HOME '.zscheme'
$zschemeHome = if ($env:ZSCHEME_HOME) { $env:ZSCHEME_HOME } else { $defaultHome }
$customHome = $zschemeHome.TrimEnd('/', '\') -ine $defaultHome.TrimEnd('/', '\')
$binDir = Join-Path $zschemeHome 'bin'

# --- RID -------------------------------------------------------------------------------------
# The release scripts ask for a RID because CI builds one per runner. Here the default is the
# machine the script runs on, which is the only one AOT can build for anyway.
if (-not $Rid) {
    $os = if ($IsWindows) { 'win' } elseif ($IsMacOS) { 'osx' } else { 'linux' }
    $arch = switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
        'X64'   { 'x64' }
        'Arm64' { 'arm64' }
        default { throw "Unsupported architecture: $([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture)" }
    }
    $Rid = "$os-$arch"
}

# .NET 10's NativeAOT links -lssl -lcrypto unconditionally on Unix, and macOS does not ship
# OpenSSL. Release CI runners happen to have it where clang finds it; a Homebrew Mac does not,
# because openssl@3 is keg-only -- so the link dies with "library 'ssl' not found". clang's
# LIBRARY_PATH is the -L equivalent and needs no project changes, and adding a directory that is
# already searchable is harmless.
if ($IsMacOS) {
    $brewPrefix = if (Test-Path '/opt/homebrew') { '/opt/homebrew' } else { '/usr/local' }
    $nativeLibDirs = @(
        Join-Path $brewPrefix 'opt/openssl@3/lib'
        Join-Path $brewPrefix 'opt/openssl/lib'
        Join-Path $brewPrefix 'opt/brotli/lib'
    ) | Where-Object { Test-Path $_ }
    if ($nativeLibDirs) {
        $env:LIBRARY_PATH = (@($nativeLibDirs) + @($env:LIBRARY_PATH) -ne $null) -join ':'
    }
}

Write-Host "Building zsup $version for $Rid ($Configuration)"

$stagingDir = Join-Path ([System.IO.Path]::GetTempPath()) "zsup-install-$Rid"
if (Test-Path $stagingDir) {
    Remove-Item -Recurse -Force $stagingDir
}

dotnet publish $Project `
    --configuration $Configuration `
    --runtime $Rid `
    --nologo `
    --output $stagingDir
if ($LASTEXITCODE -ne 0) { throw "zsup build failed for $Rid" }

$exeName = if ($IsWindows) { 'zsup.exe' } else { 'zsup' }
$builtBinary = Join-Path $stagingDir $exeName
if (-not (Test-Path $builtBinary)) {
    throw "Expected $builtBinary but the publish did not produce it"
}

# --- Install ---------------------------------------------------------------------------------
New-Item -ItemType Directory -Path $binDir -Force | Out-Null

Write-Host "Installing into $binDir"
try {
    # Stale shims are removed rather than overwritten: an old hardlink has to be unlinked before
    # a new one can take its name, and on Windows a running zsup, zs or language server locks the
    # very file being replaced -- that lands in the catch below with an explanation.
    foreach ($name in @('zsup', 'zs', 'zs-lsp')) {
        $target = Join-Path $binDir ($IsWindows ? "$name.exe" : $name)
        if (Test-Path $target) { Remove-Item -Force $target }
    }

    Copy-Item $builtBinary (Join-Path $binDir $exeName)

    if ($IsWindows) {
        # Copies: on Windows the shims must survive bin/zsup.exe being replaced wholesale by a
        # later `zsup install` or `zsup self update`, and a hardlink's bytes would not.
        foreach ($name in @('zs', 'zs-lsp')) {
            Copy-Item (Join-Path $binDir $exeName) (Join-Path $binDir "$name.exe")
        }
    } else {
        # Hardlinks, matching a release install: one inode answering to three names, so the shims
        # are the same binary by construction and can never lag behind it. They can do that
        # because the binary dispatches on argv[0].
        foreach ($name in @('zs', 'zs-lsp')) {
            New-Item -ItemType HardLink -Path (Join-Path $binDir $name) -Target (Join-Path $binDir 'zsup') | Out-Null
        }
        chmod +x (Join-Path $binDir 'zsup')
    }
} catch {
    if ($IsWindows) {
        Write-Warning "If this failed because a file is in use, stop any running zsup, zs or language server and rerun -- a running one locks its own binary."
    }
    throw
}

Remove-Item -Recurse -Force $stagingDir

# The installed binary, invoked directly so the check does not depend on PATH being set up yet.
& (Join-Path $binDir $exeName) --version
if ($LASTEXITCODE -ne 0) { throw "The installed zsup at $binDir did not run" }

# --- PATH ------------------------------------------------------------------------------------
$onPathNow = if ($IsWindows) { ($env:Path -split ';') -contains $binDir } else { ($env:PATH -split ':') -contains $binDir }

if (-not $IsWindows) {
    # Written even under -NoModifyPath, matching install.sh: there is always something to source.
    # The files embed the real install location rather than assuming ~/.zscheme, so a
    # ZSCHEME_HOME install produces an env file that actually works. Emitted only when the home
    # is not the default -- there is nothing to remember otherwise.
    $shHomeLine = if ($customHome) { 'export ZSCHEME_HOME="${ZSCHEME_HOME:-' + $zschemeHome + '}"' } else { '' }
    $envSh = @"
#!/bin/sh
# Adds the ZScheme toolchain to PATH. Sourced from your shell profile by install-zsup.ps1.
$shHomeLine
case ":`${PATH}:" in
    *:"$binDir":*) ;;
    *) export PATH="${binDir}:`$PATH" ;;
esac
"@

    $fishLines = @(
        '# Adds the ZScheme toolchain to PATH. Sourced from config.fish by install-zsup.ps1.'
        if ($customHome) {
            'if not set -q ZSCHEME_HOME'
            "    set -gx ZSCHEME_HOME `"$zschemeHome`""
            'end'
        }
        "if test -d `"$binDir`""
        "    fish_add_path --path --prepend `"$binDir`""
        'end'
    )
    $envFish = $fishLines -join "`n"

    Set-Content -Path "$zschemeHome/env" -Value $envSh
    Set-Content -Path "$zschemeHome/env.fish" -Value $envFish

    if ($onPathNow) {
        Write-Host "$binDir is already on your PATH."
    } elseif ($NoModifyPath) {
        Write-Host "Add it to your PATH with: . `"$zschemeHome/env`""
    } else {
        Write-Host "Updating your shell profile..."
        $sourceLine = ". `"$zschemeHome/env`""

        # Only ever appends once. Which file a shell actually reads varies (bash uses .bash_profile
        # for login shells on macOS and .bashrc for interactive shells on Linux), so every existing
        # candidate gets the line. ~/.profile is created if absent; the rest are only touched when
        # they already exist.
        if (-not (Test-Path "$HOME/.profile")) { New-Item -ItemType File -Path "$HOME/.profile" | Out-Null }
        foreach ($profile in @("$HOME/.profile", "$HOME/.bash_profile", "$HOME/.bash_login", "$HOME/.bashrc", "$($env:ZDOTDIR ?? $HOME)/.zshrc", "$HOME/.config/fish/config.fish")) {
            if (-not (Test-Path $profile)) { continue }
            $existing = Get-Content $profile -Raw
            if (-not $existing) { $existing = '' }
            $line = if ($profile -like '*config.fish') { "source `"$zschemeHome/env.fish`"" } else { $sourceLine }
            if (-not $existing.Contains($line)) {
                Add-Content -Path $profile -Value "`n$line"
                Write-Host "  updated $profile"
            }
        }

        # Current session too, so zsup works without restarting the shell.
        $env:PATH = "${binDir}:$env:PATH"
    }
} else {
    if ($onPathNow) {
        Write-Host "$binDir is already on your PATH."
    } elseif ($NoModifyPath) {
        Write-Host "Add this directory to your PATH manually: $binDir"
    } else {
        # The User scope specifically: $env:Path is Machine and User merged, so writing that back
        # would permanently duplicate the entire machine PATH into the user's. And the registry
        # rather than [Environment]::SetEnvironmentVariable, which expands the value on the way
        # out and stores REG_SZ on the way back in -- a user PATH holding %JAVA_HOME%\bin would
        # come back as C:\jdk-21\bin and be written back that way, silently unhooking their PATH
        # from the variable it was written against.
        $envKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Environment')
        if ($null -eq $envKey) { throw "Could not open HKCU\Environment for writing." }
        try {
            $userPath = ''
            # REG_EXPAND_SZ is what Windows itself creates the value as, and the right default for
            # a profile that somehow has no user PATH at all. An existing value keeps its own kind.
            $pathKind = [Microsoft.Win32.RegistryValueKind]::ExpandString
            $rawPath = $envKey.GetValue('Path', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            if ($null -ne $rawPath) {
                $userPath = [string]$rawPath
                $pathKind = $envKey.GetValueKind('Path')
            }

            $alreadyPresent = $false
            foreach ($entry in $userPath -split ';') {
                # Compared expanded -- a hand-written %USERPROFILE%\.zscheme\bin names this same
                # directory -- while the string written back keeps every entry as the user wrote it.
                $expanded = [Environment]::ExpandEnvironmentVariables($entry.Trim())
                if ($expanded.TrimEnd('\') -ieq $binDir.TrimEnd('\')) { $alreadyPresent = $true }
            }

            if ($alreadyPresent) {
                Write-Host "$binDir is already on your PATH."
            } elseif (($userPath.Length + $binDir.Length + 1) -gt 2000) {
                # The user PATH has a ~2048 character limit; appending past it would silently
                # truncate and destroy entries. Measured against the stored form, which is what
                # the limit is on.
                Write-Warning "Your user PATH is close to the length limit, so it was left unchanged."
                Write-Warning "Add this directory to your PATH manually: $binDir"
            } else {
                $newPath = if ($userPath -eq '') { $binDir } else { "$binDir;$userPath" }
                $envKey.SetValue('Path', $newPath, $pathKind)
                Write-Host "Added $binDir to your user PATH."
            }
        } finally {
            $envKey.Close()
        }

        # Persisted alongside PATH, or the next terminal's `zs` resolves an empty ~/.zscheme. The
        # current session already has it -- a custom home can only have come from the environment.
        if ($customHome) {
            [Environment]::SetEnvironmentVariable('ZSCHEME_HOME', $zschemeHome, 'User')
            Write-Host "Set ZSCHEME_HOME to $zschemeHome in your user environment."
        }

        # Current session, so zsup works without opening a new terminal.
        $env:Path = "$binDir;$env:Path"
    }
}

Write-Host ""
Write-Host "zsup $version is installed."
if (-not $onPathNow -and -not $IsWindows -and -not $NoModifyPath) {
    Write-Host "Restart your shell, or run: . `"$zschemeHome/env`""
}
Write-Host "Then try: zsup list"
Write-Host "For compiler development, link a dev toolchain with: pwsh ./build-dev-toolchain.ps1 -Use"
