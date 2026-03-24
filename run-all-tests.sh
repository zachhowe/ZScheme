#!/usr/bin/env bash
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "$0")" && pwd)"
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

run_step "dotnet build" \
    dotnet build "$REPO_ROOT/ZScript.slnx" --nologo

run_step "dotnet test" \
    dotnet test "$REPO_ROOT/ZScript.slnx" --no-build --nologo

run_step "stdlib tests" \
    dotnet run --no-build --project "$REPO_ROOT/src/ZScript.Cli" -- \
        test -m "$REPO_ROOT/packages/stdlib/package.zspkg" \
        --module-path "$REPO_ROOT/packages/zunit/src"

run_step "build-examples" \
    "$REPO_ROOT/build-examples.sh"

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
