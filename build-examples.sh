#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")" && pwd)"
TEMP_DIR=""
ONLY_COMBO=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --combo) ONLY_COMBO="$2"; shift 2 ;;
        *) echo "Unknown option: $1" >&2; exit 1 ;;
    esac
done

# Determine platform-specific cache root
case "$(uname -s)" in
    Linux*)  CACHE_ROOT="${XDG_CACHE_HOME:-$HOME/.cache}/zscript/pkg" ;;
    Darwin*) CACHE_ROOT="$HOME/Library/Caches/zscript/pkg" ;;
    *)       echo "Unsupported platform: $(uname -s)" >&2; exit 1 ;;
esac

cleanup() {
    if [[ -n "$TEMP_DIR" && -d "$TEMP_DIR" ]]; then
        rm -rf "$TEMP_DIR"
    fi
}
trap cleanup EXIT

# Define combinations
COMBO_NAMES=(default cached-stdlib cached-zunit cached-all)
COMBO_STDLIB=(false true false true)
COMBO_ZUNIT=(false false true true)

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
        echo "Unknown combo: $ONLY_COMBO (valid: default, cached-stdlib, cached-zunit, cached-all)" >&2
        exit 1
    fi
fi

# ==========================================================================
# One-time setup
# ==========================================================================
echo "=== Building solution ==="
dotnet build "$REPO_ROOT/ZScript.slnx" --nologo -v quiet

echo "=== Packing stdlib ==="
dotnet run --no-build --project "$REPO_ROOT/src/ZScript.Cli" -- \
    pack -m "$REPO_ROOT/packages/stdlib/package.zspkg"

echo "=== Packing ZUnit ==="
dotnet run --no-build --project "$REPO_ROOT/src/ZScript.Cli" -- \
    pack -m "$REPO_ROOT/packages/zunit/package.zspkg"

TEMP_DIR="$(mktemp -d)"
PROJECT_DIR="$TEMP_DIR/verify"
mkdir -p "$PROJECT_DIR"

RUNTIME_CSPROJ="$REPO_ROOT/src/ZScript.Runtime/ZScript.Runtime.csproj"

cat > "$PROJECT_DIR/Verify.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>preview</LangVersion>
    <Nullable>enable</Nullable>
    <OutputType>Library</OutputType>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$RUNTIME_CSPROJ" />
    <PackageReference Include="xunit" Version="2.9.3" />
  </ItemGroup>
</Project>
EOF

dotnet restore "$PROJECT_DIR/Verify.csproj" --nologo -v quiet
dotnet build "$PROJECT_DIR/Verify.csproj" --nologo -v quiet

REF_DIR="$PROJECT_DIR/bin/Debug/net10.0"
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
    CECIL_DIR="$OUT_DIR/$combo/cecil"
    mkdir -p "$TRANSPILE_DIR" "$CSC_DIR" "$IL_DIR" "$CECIL_DIR"

    # C# transpile always uses source (PersistedAssemblyBuilder DLLs reference
    # System.Private.CoreLib which the C# compiler can't resolve)
    CS_STDLIB_ARGS=(--stdlib "$REPO_ROOT/packages/stdlib/src")
    CS_ZUNIT_ARGS=(--module-path "$REPO_ROOT/packages/zunit/src")

    # IL backend respects cache flags
    IL_STDLIB_ARGS=()
    if [[ "$use_cached_stdlib" == true ]]; then
        : # omit --stdlib; compiler auto-loads from cache
    else
        IL_STDLIB_ARGS=(--stdlib "$REPO_ROOT/packages/stdlib/src")
    fi

    IL_ZUNIT_ARGS=()
    if [[ "$use_cached_zunit" == true ]]; then
        IL_ZUNIT_ARGS=(--precompiled "$CACHE_ROOT/zscript-zunit/0.1.0/zscript-zunit.dll")
    else
        IL_ZUNIT_ARGS=(--module-path "$REPO_ROOT/packages/zunit/src")
    fi

    # Cecil backend uses same args as IL
    CECIL_STDLIB_ARGS=("${IL_STDLIB_ARGS[@]+"${IL_STDLIB_ARGS[@]}"}")
    CECIL_ZUNIT_ARGS=("${IL_ZUNIT_ARGS[@]}")

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

    cecil_passed=0
    cecil_failed=0
    cecil_failures=()

    # ==================================================================
    # Phase 1: ZScript -> C# Transpile
    # ==================================================================
    echo ""
    echo "=== Phase 1: ZScript -> C# Transpile ==="

    for zs_file in "$REPO_ROOT"/examples/*.zs; do
        name="$(basename "$zs_file" .zs)"
        echo -n "  $name ... "

        cs_out="$TRANSPILE_DIR/$name.cs"
        if ! dotnet run --no-build --project "$REPO_ROOT/src/ZScript.Cli" -- \
            compile "$zs_file" "${CS_STDLIB_ARGS[@]}" \
            "${CS_ZUNIT_ARGS[@]}" \
            --ref "$REF_DIR" \
            -o "$cs_out" 2>"$ERR_FILE"; then
            echo "FAIL"
            sed 's/^/    /' "$ERR_FILE"
            transpile_failed=$((transpile_failed + 1))
            transpile_failures+=("$name")
            rm -f "$cs_out"
        elif [[ ! -f "$cs_out" ]]; then
            echo "FAIL (no .cs generated)"
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

        cp "$TRANSPILE_DIR/$name.cs" "$PROJECT_DIR/Example.cs"

        if dotnet build "$PROJECT_DIR/Verify.csproj" --no-restore --nologo -v quiet 2>"$ERR_FILE"; then
            echo "OK"
            cp "$REF_DIR/Verify.dll" "$CSC_DIR/$name.dll"
            csc_passed=$((csc_passed + 1))
        else
            echo "FAIL"
            sed 's/^/    /' "$ERR_FILE"
            csc_failed=$((csc_failed + 1))
            csc_failures+=("$name")
        fi

        rm -f "$PROJECT_DIR/Example.cs"
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
        echo -n "  $name ... "

        il_out="$IL_DIR/$name.dll"
        if ! dotnet run --no-build --project "$REPO_ROOT/src/ZScript.Cli" -- \
            compile "$zs_file" --backend il "${IL_STDLIB_ARGS[@]}" \
            "${IL_ZUNIT_ARGS[@]}" \
            --ref "$REF_DIR" \
            -o "$il_out" 2>"$ERR_FILE"; then
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

    # ==================================================================
    # Phase 4: ZScript -> Cecil Direct Compile
    # ==================================================================
    echo ""
    echo "=== Phase 4: ZScript -> Cecil Direct Compile ==="

    for zs_file in "$REPO_ROOT"/examples/*.zs; do
        name="$(basename "$zs_file" .zs)"
        echo -n "  $name ... "

        cecil_out="$CECIL_DIR/$name.dll"
        if ! dotnet run --no-build --project "$REPO_ROOT/src/ZScript.Cli" -- \
            compile "$zs_file" --backend cecil "${CECIL_STDLIB_ARGS[@]}" \
            "${CECIL_ZUNIT_ARGS[@]}" \
            --ref "$REF_DIR" \
            -o "$cecil_out" 2>"$ERR_FILE"; then
            echo "FAIL"
            sed 's/^/    /' "$ERR_FILE"
            cecil_failed=$((cecil_failed + 1))
            cecil_failures+=("$name")
        else
            echo "OK"
            cecil_passed=$((cecil_passed + 1))
        fi
    done

    total_cecil=$((cecil_passed + cecil_failed))
    echo "  $cecil_passed/$total_cecil passed"

    # Per-combo summary
    echo ""
    echo "--- $combo summary ---"
    echo "  ZScript -> C# Transpile:  $transpile_passed/$total_transpile passed"
    echo "  C# Compile (csc):         $csc_passed/$total_csc passed"
    echo "  IL Direct Compile:         $il_passed/$total_il passed"
    echo "  Cecil Direct Compile:      $cecil_passed/$total_cecil passed"

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
    if [[ ${#cecil_failures[@]} -gt 0 ]]; then
        combo_failures+=("cecil: ${cecil_failures[*]}")
    fi

    combo_total_failed=$((transpile_failed + csc_failed + il_failed + cecil_failed))
    combo_total_passed=$((transpile_passed + csc_passed + il_passed + cecil_passed))
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
