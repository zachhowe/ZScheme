#!/usr/bin/env sh
# ZScheme installer for Linux and macOS.
#
#   curl -fsSL https://raw.githubusercontent.com/zachhowe/ZScheme/master/install.sh | sh
#
# Deliberately POSIX sh rather than bash: piping into `sh` has to work under dash and ash, which
# are /bin/sh on Debian/Ubuntu and Alpine. Everything beyond placing zsup is delegated to
# `zsup install`, so there is only one implementation of the real work.
set -eu

REPO="${ZSCHEME_GITHUB_REPO:-zachhowe/ZScheme}"
ZSCHEME_HOME="${ZSCHEME_HOME:-$HOME/.zscheme}"
BIN_DIR="$ZSCHEME_HOME/bin"
# Deliberately not ZSCHEME_VERSION: that selects which installed toolchain the `zs` shim runs, and
# plenty of people have it exported to a name like `dev`. Reusing it here would turn a re-run of the
# installer into a download of "zsup-dev-linux-x64.tar.gz".
VERSION="${ZSCHEME_INSTALL_VERSION:-}"
MODIFY_PATH=1

usage() {
    cat <<EOF
Usage: install.sh [options]

Options:
  --version <X.Y.Z>   Install a specific version (default: the latest release)
  --no-modify-path    Do not touch your shell profile
  -h, --help          Show this help

Environment:
  ZSCHEME_INSTALL_VERSION   Same as --version. Not ZSCHEME_VERSION, which selects the
                            installed toolchain that \`zs\` runs.
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
        # The count is checked first: `shift 2` with one argument left aborts under `set -eu` with
        # the shell's own error instead of the usage message.
        --version)
            [ $# -ge 2 ] || { echo "error: --version needs a value" >&2; usage >&2; exit 1; }
            VERSION="$2"; shift 2 ;;
        --no-modify-path) MODIFY_PATH=0; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "error: unknown option: $1" >&2; usage >&2; exit 1 ;;
    esac
done

say() { printf '%s\n' "$*"; }
err() { printf 'error: %s\n' "$*" >&2; exit 1; }
need() { command -v "$1" >/dev/null 2>&1 || err "this installer needs '$1'"; }

# --- Detect the platform ------------------------------------------------------------------
case "$(uname -s)" in
    Linux)  os="linux" ;;
    Darwin) os="osx" ;;
    *)      err "unsupported operating system: $(uname -s)" ;;
esac

case "$(uname -m)" in
    x86_64|amd64)  arch="x64" ;;
    aarch64|arm64) arch="arm64" ;;
    *)             err "unsupported architecture: $(uname -m)" ;;
esac

RID="$os-$arch"

# --- Pick a downloader --------------------------------------------------------------------
if command -v curl >/dev/null 2>&1; then
    fetch() { curl -fsSL "$1" -o "$2"; }
    fetch_stdout() { curl -fsSL "$1"; }
elif command -v wget >/dev/null 2>&1; then
    fetch() { wget -qO "$2" "$1"; }
    fetch_stdout() { wget -qO- "$1"; }
else
    err "this installer needs either curl or wget"
fi

need tar

# --- Resolve the version ------------------------------------------------------------------
# TAG is the URL segment the assets live under; VERSION is the bare version in their names. They
# are the same today, and keeping them apart is what makes the v-prefix tolerance below work at all
# -- stripping the prefix and then using it as the tag would 404 on every download.
if [ -z "$VERSION" ]; then
    say "Looking up the latest ZScheme release..."
    TAG=$(fetch_stdout "https://api.github.com/repos/$REPO/releases/latest" \
        | sed -n 's/.*"tag_name"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' \
        | head -n 1)
    [ -n "$TAG" ] || err "could not determine the latest release"
    VERSION="${TAG#v}"
else
    # `--version v0.4.0` has to work too: zsup's own ReleaseRef tolerates the prefix, and this is
    # the same split -- the tag keeps whatever was typed, the asset name gets it stripped.
    TAG="$VERSION"
    VERSION="${VERSION#v}"
fi

BASE_URL="${ZSCHEME_DIST_BASE_URL:-https://github.com/$REPO/releases/download}"
ASSET="zsup-$VERSION-$RID.tar.gz"

say "Installing ZScheme $VERSION for $RID into $ZSCHEME_HOME"

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT INT TERM

# --- Download and verify zsup -------------------------------------------------------------
say "Downloading $ASSET..."
fetch "$BASE_URL/$TAG/$ASSET" "$tmp/$ASSET" || err "could not download $ASSET"

# Verification is mandatory. Downgrading to "warn and install anyway" would mean anyone able to
# block or 404 a single URL could turn it off entirely -- and the warning would scroll past
# unnoticed in a `curl | sh`. Set ZSCHEME_SKIP_VERIFY=1 to override deliberately.
if [ "${ZSCHEME_SKIP_VERIFY:-0}" = "1" ]; then
    say "warning: ZSCHEME_SKIP_VERIFY is set; not verifying the download"
else
    fetch "$BASE_URL/$TAG/SHA256SUMS" "$tmp/SHA256SUMS" 2>/dev/null \
        || err "could not download SHA256SUMS for $VERSION; refusing to install unverified"

    # Exact field comparison rather than a grep pattern: asset names contain dots, which would
    # otherwise be regex wildcards. The leading '*' strip handles sha256sum's binary-mode form.
    expected=$(awk -v want="$ASSET" '{ n = $2; sub(/^\*/, "", n); if (n == want) { print $1; exit } }' "$tmp/SHA256SUMS")
    [ -n "$expected" ] || err "SHA256SUMS for $VERSION does not list $ASSET; refusing to install unverified"

    if command -v sha256sum >/dev/null 2>&1; then
        actual=$(sha256sum "$tmp/$ASSET" | awk '{print $1}')
    elif command -v shasum >/dev/null 2>&1; then
        actual=$(shasum -a 256 "$tmp/$ASSET" | awk '{print $1}')
    else
        err "need sha256sum or shasum to verify the download (set ZSCHEME_SKIP_VERIFY=1 to skip)"
    fi

    [ "$actual" = "$expected" ] \
        || err "checksum mismatch for $ASSET (expected $expected, got $actual)"
fi

mkdir -p "$BIN_DIR"
tar -xzf "$tmp/$ASSET" -C "$BIN_DIR"
chmod +x "$BIN_DIR/zsup"

# --- Everything else is zsup's job --------------------------------------------------------
# The tag, not the stripped version: zsup builds its own download URLs from what it is given, and
# it applies the same v-prefix rule as above to arrive at the name it installs under.
say "Installing the toolchain..."
"$BIN_DIR/zsup" install "$TAG" --force

# --- PATH ---------------------------------------------------------------------------------
# The generated files embed the real install location rather than assuming ~/.zscheme, so a
# ZSCHEME_HOME install produces an env file that actually works. Written even with
# --no-modify-path, so there is always something to source.
cat > "$ZSCHEME_HOME/env" <<EOF
#!/bin/sh
# Adds the ZScheme toolchain to PATH. Sourced from your shell profile by install.sh.
case ":\${PATH}:" in
    *:"$BIN_DIR":*) ;;
    *) export PATH="$BIN_DIR:\$PATH" ;;
esac
EOF

cat > "$ZSCHEME_HOME/env.fish" <<EOF
# Adds the ZScheme toolchain to PATH. Sourced from config.fish by install.sh.
if test -d "$BIN_DIR"
    fish_add_path --path --prepend "$BIN_DIR"
end
EOF

add_source_line() {
    # \$1 = profile file, \$2 = line to add. Only ever appends once.
    [ -f "$1" ] || return 0
    if ! grep -qsF "$2" "$1"; then
        printf '\n%s\n' "$2" >> "$1"
        say "  updated $1"
    fi
}

if [ "$MODIFY_PATH" -eq 1 ]; then
    say "Updating your shell profile..."
    SOURCE_LINE=". \"$ZSCHEME_HOME/env\""
    FISH_SOURCE_LINE="source \"$ZSCHEME_HOME/env.fish\""

    # ~/.profile is created if absent; the rest are only touched when they already exist. Which
    # file a shell actually reads varies (bash uses .bash_profile for login shells on macOS and
    # .bashrc for interactive shells on Linux), so every existing candidate gets the line.
    [ -f "$HOME/.profile" ] || : > "$HOME/.profile"
    add_source_line "$HOME/.profile" "$SOURCE_LINE"
    add_source_line "$HOME/.bash_profile" "$SOURCE_LINE"
    add_source_line "$HOME/.bash_login" "$SOURCE_LINE"
    add_source_line "$HOME/.bashrc" "$SOURCE_LINE"
    add_source_line "${ZDOTDIR:-$HOME}/.zshrc" "$SOURCE_LINE"
    add_source_line "$HOME/.config/fish/config.fish" "$FISH_SOURCE_LINE"
fi

say ""
say "ZScheme $VERSION is installed."
if [ "$MODIFY_PATH" -eq 1 ]; then
    say "Restart your shell, or run: . \"$ZSCHEME_HOME/env\""
else
    say "Add it to your PATH with: . \"$ZSCHEME_HOME/env\""
fi
say "Then try: zs --version"
