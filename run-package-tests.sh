#!/usr/bin/env bash
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "$0")" && pwd)"
DEBUG_FLAG=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --debug) DEBUG_FLAG="--debug"; shift ;;
        --help|-h)
            echo "Usage: $0 [--debug]"
            echo "  --debug   Enable debug logging for the ZScript compiler"
            exit 0
            ;;
        -*) echo "Unknown option: $1" >&2; exit 1 ;;
        *) echo "Unknown argument: $1" >&2; exit 1 ;;
    esac
done

failures=0
results=()

run_step() {
    local label="$1"
    shift
    echo ""
    echo "================================================================"
    echo "=== $label ==="
    echo "================================================================"
    if "$@"; then
        results+=("PASS: $label")
    else
        results+=("FAIL: $label")
        failures=$((failures + 1))
    fi
}

run_step "stdlib tests" \
    dotnet run --no-build --project "$REPO_ROOT/src/ZScript.Cli" -- \
        test -m "$REPO_ROOT/packages/stdlib/package.zspkg" \
        --module-path "$REPO_ROOT/packages/zunit/src" \
        --package-path "$REPO_ROOT/packages/zunit" $DEBUG_FLAG

run_step "http tests" \
    dotnet run --no-build --project "$REPO_ROOT/src/ZScript.Cli" -- \
        test -m "$REPO_ROOT/packages/http/package.zspkg" \
        --module-path "$REPO_ROOT/packages/zunit/src" \
        --package-path "$REPO_ROOT/packages/zunit" \
        --module-path "$REPO_ROOT/packages/stdlib/src" \
        --package-path "$REPO_ROOT/packages/stdlib" $DEBUG_FLAG

echo ""
echo "================================================================"
echo "=== Summary ==="
echo "================================================================"
for r in "${results[@]}"; do
    echo "  $r"
done
echo ""

if [[ $failures -gt 0 ]]; then
    echo "$failures step(s) failed."
    exit 1
else
    echo "All steps passed."
fi
