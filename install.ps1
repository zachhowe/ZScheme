<#
.SYNOPSIS
    ZScheme installer for Windows.
.DESCRIPTION
    irm https://raw.githubusercontent.com/zachhowe/ZScheme/master/install.ps1 | iex

    NOTE: unlike every other .ps1 in this repo, this script deliberately targets Windows
    PowerShell 5.1 and therefore has no pwsh 7.6 version guard. It runs before anything is
    installed, so it has to work on a stock Windows box. Do not "fix" that by adding the guard,
    and do not use PowerShell 7 syntax (??, ternaries, null-conditionals) below.
#>
param(
    [string]$Version,
    [switch]$NoModifyPath
)

$ErrorActionPreference = 'Stop'
# Required on Windows PowerShell 5.1, whose default is TLS 1.0.
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$repo = if ($env:ZSCHEME_GITHUB_REPO) { $env:ZSCHEME_GITHUB_REPO } else { 'zachhowe/ZScheme' }
$zschemeHome = if ($env:ZSCHEME_HOME) { $env:ZSCHEME_HOME } else { Join-Path $env:USERPROFILE '.zscheme' }
$binDir = Join-Path $zschemeHome 'bin'

# --- Detect the architecture ----------------------------------------------------------------
switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
    'X64'   { $rid = 'win-x64' }
    'Arm64' { $rid = 'win-arm64' }
    default { throw "Unsupported architecture: $([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture)" }
}

# --- Resolve the version --------------------------------------------------------------------
# Deliberately not ZSCHEME_VERSION: that selects which installed toolchain the `zs` shim runs, so
# anyone who has it set to a name like `dev` would re-run this installer and get a download of
# "zsup-dev-win-x64.zip".
if (-not $Version) { $Version = $env:ZSCHEME_INSTALL_VERSION }

# $tag is the URL segment the assets live under; $Version is the bare version in their names. They
# are the same today, and keeping them apart is what makes the v-prefix tolerance below work at all
# -- stripping the prefix and then using it as the tag would 404 on every download.
if (-not $Version) {
    Write-Host "Looking up the latest ZScheme release..."
    $latest = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/latest" -UseBasicParsing -Headers @{ 'User-Agent' = 'zscheme-installer' }
    $tag = $latest.tag_name
    if (-not $tag) { throw "Could not determine the latest release." }
    if ($tag.StartsWith('v')) { $Version = $tag.Substring(1) } else { $Version = $tag }
} else {
    $tag = $Version
}

$baseUrl = if ($env:ZSCHEME_DIST_BASE_URL) { $env:ZSCHEME_DIST_BASE_URL } else { "https://github.com/$repo/releases/download" }
$asset = "zsup-$Version-$rid.zip"

Write-Host "Installing ZScheme $Version for $rid into $zschemeHome"

$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("zscheme-install-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $tmp -Force | Out-Null

try {
    # --- Download and verify zsup -----------------------------------------------------------
    $assetPath = Join-Path $tmp $asset
    Write-Host "Downloading $asset..."
    Invoke-WebRequest -Uri "$baseUrl/$tag/$asset" -OutFile $assetPath -UseBasicParsing

    # Verification is mandatory. Downgrading to "warn and install anyway" would let anyone able to
    # block or 404 a single URL turn it off entirely. Set ZSCHEME_SKIP_VERIFY=1 to override.
    if ($env:ZSCHEME_SKIP_VERIFY -eq '1') {
        Write-Warning "ZSCHEME_SKIP_VERIFY is set; not verifying the download."
    } else {
        try {
            $sums = (Invoke-WebRequest -Uri "$baseUrl/$tag/SHA256SUMS" -UseBasicParsing).Content
        } catch {
            throw "Could not download SHA256SUMS for ${Version}; refusing to install unverified."
        }

        $expected = $null
        foreach ($line in $sums -split "`n") {
            $parts = ($line.Trim() -split '\s+')
            if ($parts.Length -ge 2 -and $parts[-1].TrimStart('*') -eq $asset) {
                $expected = $parts[0]
                break
            }
        }
        if (-not $expected) {
            throw "SHA256SUMS for $Version does not list ${asset}; refusing to install unverified."
        }

        $actual = (Get-FileHash $assetPath -Algorithm SHA256).Hash.ToLower()
        if ($actual -ne $expected.ToLower()) {
            throw "Checksum mismatch for ${asset}: expected $expected, got $actual"
        }
    }

    New-Item -ItemType Directory -Path $binDir -Force | Out-Null
    # -Force so re-running the installer over an existing bin dir works.
    Expand-Archive -Path $assetPath -DestinationPath $binDir -Force

    # --- Everything else is zsup's job ------------------------------------------------------
    # The tag, not the stripped version: zsup builds its own download URLs from what it is given,
    # and it applies the same v-prefix rule as above to arrive at the name it installs under.
    Write-Host "Installing the toolchain..."
    & (Join-Path $binDir 'zsup.exe') install $tag --force
    if ($LASTEXITCODE -ne 0) { throw "zsup install exited $LASTEXITCODE" }
} finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}

# --- PATH -----------------------------------------------------------------------------------
if (-not $NoModifyPath) {
    # Read the User scope specifically. $env:Path is Machine and User merged, so writing that
    # back would permanently duplicate the entire machine PATH into the user's.
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    if ($null -eq $userPath) { $userPath = '' }

    $alreadyPresent = $false
    foreach ($entry in $userPath -split ';') {
        if ($entry.Trim().TrimEnd('\') -ieq $binDir.TrimEnd('\')) { $alreadyPresent = $true }
    }

    if ($alreadyPresent) {
        Write-Host "$binDir is already on your PATH."
    } elseif (($userPath.Length + $binDir.Length + 1) -gt 2000) {
        # The user PATH is stored as REG_EXPAND_SZ with a ~2048 character limit; appending past
        # it would silently truncate and destroy entries.
        Write-Warning "Your user PATH is close to the length limit, so it was left unchanged."
        Write-Warning "Add this directory to your PATH manually: $binDir"
    } else {
        if ($userPath -eq '') { $newPath = $binDir } else { $newPath = "$binDir;$userPath" }
        [Environment]::SetEnvironmentVariable('Path', $newPath, 'User')
        Write-Host "Added $binDir to your user PATH."
    }

    # Current session, so `zs` works without opening a new terminal.
    $env:Path = "$binDir;$env:Path"
}

Write-Host ""
Write-Host "ZScheme $Version is installed."
if ($NoModifyPath) {
    Write-Host "Add this to your PATH: $binDir"
} else {
    Write-Host "Open a new terminal, then try: zs --version"
}
