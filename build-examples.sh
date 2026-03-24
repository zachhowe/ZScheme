#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")" && pwd)"
TEMP_DIR=""

USE_CACHED_STDLIB=false
USE_CACHED_ZUNIT=false

while [[ $# -gt 0 ]]; do
    case "$1" in
        --cached-stdlib) USE_CACHED_STDLIB=true; shift ;;
        --cached-zunit)  USE_CACHED_ZUNIT=true; shift ;;
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

echo "=== Building solution ==="
dotnet build "$REPO_ROOT/ZScript.slnx" --nologo -v quiet

# Pack cached packages if requested
if [[ "$USE_CACHED_STDLIB" == true ]]; then
    echo "=== Packing stdlib ==="
    dotnet run --no-build --project "$REPO_ROOT/src/ZScript.Cli" -- \
        pack -m "$REPO_ROOT/packages/stdlib/package.zspkg"
fi

if [[ "$USE_CACHED_ZUNIT" == true ]]; then
    echo "=== Packing ZUnit ==="
    dotnet run --no-build --project "$REPO_ROOT/src/ZScript.Cli" -- \
        pack -m "$REPO_ROOT/packages/zunit/package.zspkg"
fi

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
CS_OUT_DIR="$TEMP_DIR/transpiled"
mkdir -p "$CS_OUT_DIR"

# Build compile args based on caching flags
STDLIB_ARGS=()
if [[ "$USE_CACHED_STDLIB" == true ]]; then
    : # omit --stdlib; compiler auto-loads from cache
else
    STDLIB_ARGS+=(--stdlib "$REPO_ROOT/packages/stdlib/src")
fi

ZUNIT_ARGS=()
if [[ "$USE_CACHED_ZUNIT" == true ]]; then
    ZUNIT_ARGS+=(--precompiled "$CACHE_ROOT/zscript-zunit/0.1.0/zscript-zunit.dll")
else
    ZUNIT_ARGS+=(--module-path "$REPO_ROOT/packages/zunit/src")
fi

# Track results per phase
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

# ==========================================================================
# Phase 1: ZScript -> C# Transpile
# ==========================================================================
echo ""
echo "=== Phase 1: ZScript -> C# Transpile ==="

for zs_file in "$REPO_ROOT"/examples/*.zs; do
    name="$(basename "$zs_file" .zs)"
    echo -n "  $name ... "

    cs_out="$CS_OUT_DIR/$name.cs"
    if ! dotnet run --no-build --project "$REPO_ROOT/src/ZScript.Cli" -- \
        compile "$zs_file" "${STDLIB_ARGS[@]}" \
        "${ZUNIT_ARGS[@]}" \
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

# ==========================================================================
# Phase 2: C# Compile (csc)
# ==========================================================================
echo ""
echo "=== Phase 2: C# Compile (csc) ==="

for name in "${transpile_succeeded_names[@]}"; do
    echo -n "  $name ... "

    cp "$CS_OUT_DIR/$name.cs" "$PROJECT_DIR/Example.cs"

    if dotnet build "$PROJECT_DIR/Verify.csproj" --no-restore --nologo -v quiet 2>"$ERR_FILE"; then
        echo "OK"
        cp "$PROJECT_DIR/Example.cs" "$REPO_ROOT/examples/$name.cs"
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

# ==========================================================================
# Phase 3: ZScript -> IL Direct Compile
# ==========================================================================
echo ""
echo "=== Phase 3: ZScript -> IL Direct Compile ==="

for zs_file in "$REPO_ROOT"/examples/*.zs; do
    name="$(basename "$zs_file" .zs)"
    echo -n "  $name ... "

    il_out="$PROJECT_DIR/$name.dll"
    if ! dotnet run --no-build --project "$REPO_ROOT/src/ZScript.Cli" -- \
        compile "$zs_file" --backend il "${STDLIB_ARGS[@]}" \
        "${ZUNIT_ARGS[@]}" \
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

    rm -f "$PROJECT_DIR/$name.dll" "$PROJECT_DIR/$name.exe"
done

total_il=$((il_passed + il_failed))
echo "  $il_passed/$total_il passed"

# ==========================================================================
# Summary
# ==========================================================================
echo ""
echo "=== Summary ==="
echo "  ZScript -> C# Transpile: $transpile_passed/$total_transpile passed"
echo "  C# Compile (csc):        $csc_passed/$total_csc passed"
echo "  IL Direct Compile:        $il_passed/$total_il passed"

has_failures=false
if [[ ${#transpile_failures[@]} -gt 0 ]]; then
    echo ""
    echo "Transpile failures: ${transpile_failures[*]}"
    has_failures=true
fi
if [[ ${#csc_failures[@]} -gt 0 ]]; then
    echo "C# compile failures: ${csc_failures[*]}"
    has_failures=true
fi
if [[ ${#il_failures[@]} -gt 0 ]]; then
    echo "IL compile failures: ${il_failures[*]}"
    has_failures=true
fi
if [[ "$has_failures" == true ]]; then
    exit 1
fi
