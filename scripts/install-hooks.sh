#!/usr/bin/env bash
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$REPO_ROOT"
# git-path may be relative (.git/hooks) or absolute (worktrees); resolve from REPO_ROOT.
HOOKS_DIR="$(cd "$REPO_ROOT" && realpath -m "$(git rev-parse --git-path hooks)")"

# Ensure dotnet is available
if ! command -v dotnet &>/dev/null; then
    echo "Error: dotnet is not installed or not in PATH." >&2
    exit 1
fi

# Restore local tools (CSharpier) so the first commit doesn't pay for it
if ! dotnet tool restore >/dev/null 2>&1; then
    echo "Error: 'dotnet tool restore' failed; CSharpier is unavailable." >&2
    exit 1
fi

# Ensure .git/hooks directory exists
mkdir -p "$HOOKS_DIR"

# Copy pre-commit hook
HOOK_FILE="$HOOKS_DIR/pre-commit"
cp "$SCRIPT_DIR/hooks/pre-commit" "$HOOK_FILE"
chmod +x "$HOOK_FILE"
echo "Pre-commit hook installed at $HOOK_FILE"
