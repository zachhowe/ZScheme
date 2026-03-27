#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")" && pwd)"
TEMP_DIR=""
ONLY_COMBO=""
DEBUG_FLAG=""
EXAMPLES=()

while [[ $# -gt 0 ]]; do
    case "$1" in
        --combo) ONLY_COMBO="$2"; shift 2 ;;
        --debug) DEBUG_FLAG="--debug"; shift ;;
        --help|-h)
            echo "Usage: $0 [--combo NAME] [--debug] [EXAMPLE ...]"
            echo "  --combo NAME   Run only the specified combination (default, cached-all)"
            echo "  --debug        Enable debug logging for the ZScript compiler"
            echo "  EXAMPLE        One or more example names (without .zs) to build. If omitted, all examples are built."
            exit 0
            ;;
        -*) echo "Unknown option: $1" >&2; exit 1 ;;
        *) EXAMPLES+=("$1"); shift ;;
    esac
done

# Helper: check if an example should be built
should_build() {
    local name="$1"
    if [[ ${#EXAMPLES[@]} -eq 0 ]]; then
        return 0
    fi
    for ex in "${EXAMPLES[@]}"; do
        if [[ "$ex" == "$name" ]]; then
            return 0
        fi
    done
    return 1
}

# Cache root must match ZScriptPaths.GetPackageCacheRoot() in the compiler
CACHE_ROOT="$HOME/.zscript/cache/pkg"

cleanup() {
    if [[ -n "$TEMP_DIR" && -d "$TEMP_DIR" ]]; then
        rm -rf "$TEMP_DIR"
    fi
}
trap cleanup EXIT

# Define combinations
COMBO_NAMES=(default cached-all)
COMBO_STDLIB=(false true)
COMBO_ZUNIT=(false true)

# Filter to single combo if requested
if [[ -n "$ONLY_COMBO" ]]; then
    found=false
    for i in "${!COMBO_NAMES[@]}"; do
        if [[ "${COMBO_NAMES[$i]}" == "$ONLY_COMBO" ]]; then
            COMBO_NAMES=("${COMBO_NAMES[$i]}")
            COMBO_STDLIB=("${COMBO_STDLIB[$i]}")
            COMBO_ZUNIT=("${COMBO_ZUNIT[$i]}")
            found=true
            break
        fi
    done
    if [[ "$found" != true ]]; then
        echo "Unknown combo: $ONLY_COMBO (valid: default, cached-all)" >&2
        exit 1
    fi
fi

# ==========================================================================
# One-time setup
# ==========================================================================
echo "=== Building solution ==="
dotnet build "$REPO_ROOT/ZScript.slnx" --nologo -v quiet

echo "=== Installing stdlib ==="
dotnet run --no-build --project "$REPO_ROOT/src/ZScript.Cli" -- \
    install -m "$REPO_ROOT/packages/stdlib/package.zspkg" $DEBUG_FLAG

echo "=== Installing ZUnit ==="
dotnet run --no-build --project "$REPO_ROOT/src/ZScript.Cli" -- \
    install -m "$REPO_ROOT/packages/zunit/package.zspkg" $DEBUG_FLAG

TEMP_DIR="$(mktemp -d)"
ERR_FILE="$TEMP_DIR/stderr.log"

# Clean output directory
OUT_DIR="$REPO_ROOT/examples/out"
rm -rf "$OUT_DIR"

# Grand totals
grand_passed=0
grand_failed=0
grand_results=()

# ==========================================================================
# Loop over combinations
# ==========================================================================
for ci in "${!COMBO_NAMES[@]}"; do
    combo="${COMBO_NAMES[$ci]}"
    use_cached_stdlib="${COMBO_STDLIB[$ci]}"
    use_cached_zunit="${COMBO_ZUNIT[$ci]}"

    echo ""
    echo "========================================"
    echo "=== Combination: $combo ==="
    echo "========================================"

    TRANSPILE_DIR="$OUT_DIR/$combo/transpile"
    CSC_DIR="$OUT_DIR/$combo/csc"
    IL_DIR="$OUT_DIR/$combo/il"
    mkdir -p "$TRANSPILE_DIR" "$CSC_DIR" "$IL_DIR"

    # C# transpile always uses source (PersistedAssemblyBuilder DLLs reference
    # System.Private.CoreLib which the C# compiler can't resolve)
    CS_STDLIB_ARGS=(--package-path "$REPO_ROOT/packages/stdlib")
    CS_ZUNIT_ARGS=(--module-path "$REPO_ROOT/packages/zunit/src")

    # IL backend respects cache flags
    IL_STDLIB_ARGS=()
    if [[ "$use_cached_stdlib" == true ]]; then
        : # omit --package-path; compiler auto-loads from cache
    else
        IL_STDLIB_ARGS=(--package-path "$REPO_ROOT/packages/stdlib")
    fi

    IL_ZUNIT_ARGS=()
    if [[ "$use_cached_zunit" == true ]]; then
        IL_ZUNIT_ARGS=(--precompiled "$CACHE_ROOT/zscript-zunit/0.1.0/zscript-zunit.dll")
    else
        IL_ZUNIT_ARGS=(--module-path "$REPO_ROOT/packages/zunit/src")
    fi

    # Per-combo trackers
    transpile_passed=0
    transpile_failed=0
    transpile_failures=()
    transpile_succeeded_names=()

    csc_passed=0
    csc_failed=0
    csc_failures=()

    il_passed=0
    il_failed=0
    il_failures=()

    # ==================================================================
    # Phase 1: ZScript -> C# Transpile (emit project)
    # ==================================================================
    echo ""
    echo "=== Phase 1: ZScript -> C# Transpile ==="

    for zs_file in "$REPO_ROOT"/examples/*.zs; do
        name="$(basename "$zs_file" .zs)"
        should_build "$name" || continue
        echo -n "  $name ... "

        project_out="$TRANSPILE_DIR/$name"
        if ! dotnet run --no-build --project "$REPO_ROOT/src/ZScript.Cli" -- \
            compile "$zs_file" "${CS_STDLIB_ARGS[@]}" \
            "${CS_ZUNIT_ARGS[@]}" \
            --emit-project --output-type Library --lang-version preview \
            --nuget xunit:2.9.3 \
            -o "$project_out" $DEBUG_FLAG 2>"$ERR_FILE"; then
            echo "FAIL"
            sed 's/^/    /' "$ERR_FILE"
            transpile_failed=$((transpile_failed + 1))
            transpile_failures+=("$name")
            rm -rf "$project_out"
        elif [[ ! -f "$project_out/$name.csproj" ]]; then
            echo "FAIL (no project generated)"
            transpile_failed=$((transpile_failed + 1))
            transpile_failures+=("$name")
        else
            echo "OK"
            transpile_passed=$((transpile_passed + 1))
            transpile_succeeded_names+=("$name")
        fi
    done

    total_transpile=$((transpile_passed + transpile_failed))
    echo "  $transpile_passed/$total_transpile passed"

    # ==================================================================
    # Phase 2: C# Compile (csc)
    # ==================================================================
    echo ""
    echo "=== Phase 2: C# Compile (csc) ==="

    for name in "${transpile_succeeded_names[@]}"; do
        echo -n "  $name ... "

        if dotnet build "$TRANSPILE_DIR/$name/$name.csproj" --nologo -v quiet 2>"$ERR_FILE"; then
            echo "OK"
            cp "$TRANSPILE_DIR/$name/bin/Debug/net10.0/$name.dll" "$CSC_DIR/$name.dll"
            csc_passed=$((csc_passed + 1))
        else
            echo "FAIL"
            sed 's/^/    /' "$ERR_FILE"
            csc_failed=$((csc_failed + 1))
            csc_failures+=("$name")
        fi
    done

    total_csc=$((csc_passed + csc_failed))
    echo "  $csc_passed/$total_csc passed"

    # ==================================================================
    # Phase 3: ZScript -> IL Direct Compile
    # ==================================================================
    echo ""
    echo "=== Phase 3: ZScript -> IL Direct Compile ==="

    for zs_file in "$REPO_ROOT"/examples/*.zs; do
        name="$(basename "$zs_file" .zs)"
        should_build "$name" || continue
        echo -n "  $name ... "

        il_out="$IL_DIR/$name.dll"
        if ! dotnet run --no-build --project "$REPO_ROOT/src/ZScript.Cli" -- \
            compile "$zs_file" --backend il "${IL_STDLIB_ARGS[@]}" \
            "${IL_ZUNIT_ARGS[@]}" \
            --nuget xunit:2.9.3 \
            -o "$il_out" $DEBUG_FLAG 2>"$ERR_FILE"; then
            echo "FAIL"
            sed 's/^/    /' "$ERR_FILE"
            il_failed=$((il_failed + 1))
            il_failures+=("$name")
        else
            echo "OK"
            il_passed=$((il_passed + 1))
        fi
    done

    total_il=$((il_passed + il_failed))
    echo "  $il_passed/$total_il passed"

    # Per-combo summary
    echo ""
    echo "--- $combo summary ---"
    echo "  ZScript -> C# Transpile:  $transpile_passed/$total_transpile passed"
    echo "  C# Compile (csc):         $csc_passed/$total_csc passed"
    echo "  IL Direct Compile:         $il_passed/$total_il passed"

    combo_failures=()
    if [[ ${#transpile_failures[@]} -gt 0 ]]; then
        combo_failures+=("transpile: ${transpile_failures[*]}")
    fi
    if [[ ${#csc_failures[@]} -gt 0 ]]; then
        combo_failures+=("csc: ${csc_failures[*]}")
    fi
    if [[ ${#il_failures[@]} -gt 0 ]]; then
        combo_failures+=("il: ${il_failures[*]}")
    fi

    combo_total_failed=$((transpile_failed + csc_failed + il_failed))
    combo_total_passed=$((transpile_passed + csc_passed + il_passed))
    grand_passed=$((grand_passed + combo_total_passed))
    grand_failed=$((grand_failed + combo_total_failed))

    if [[ $combo_total_failed -gt 0 ]]; then
        grand_results+=("FAIL: $combo ($combo_total_failed failures)")
        for f in "${combo_failures[@]}"; do
            grand_results+=("       $f")
        done
    else
        grand_results+=("PASS: $combo")
    fi
done

# ==========================================================================
# Grand Summary
# ==========================================================================
echo ""
echo "========================================"
echo "=== Grand Summary ==="
echo "========================================"
for r in "${grand_results[@]}"; do
    echo "  $r"
done
echo ""
echo "  Total: $grand_passed passed, $grand_failed failed"

if [[ $grand_failed -gt 0 ]]; then
    exit 1
fi
