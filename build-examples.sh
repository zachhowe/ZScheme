#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")" && pwd)"
TEMP_DIR=""

cleanup() {
    if [[ -n "$TEMP_DIR" && -d "$TEMP_DIR" ]]; then
        rm -rf "$TEMP_DIR"
    fi
}
trap cleanup EXIT

echo "=== Building solution ==="
dotnet build "$REPO_ROOT/ZScript.slnx" --nologo -v quiet

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

cs_passed=0
cs_failed=0
cs_failures=()
il_passed=0
il_failed=0
il_failures=()

for zs_file in "$REPO_ROOT"/examples/*.zs; do
    name="$(basename "$zs_file" .zs)"

    # --- C# backend ---
    echo -n "  $name (C#) ... "

    cs_out="$PROJECT_DIR/$name.cs"
    if ! dotnet run --no-build --project "$REPO_ROOT/src/ZScript.Cli" -- \
        compile "$zs_file" --stdlib "$REPO_ROOT/src/ZScript.StdLib" \
        --module-path "$REPO_ROOT/src/ZScript.ZUnit" \
        --ref "$REF_DIR" \
        -o "$cs_out" 2>/dev/null; then
        echo "FAIL (zs compile)"
        cs_failed=$((cs_failed + 1))
        cs_failures+=("$name")
        rm -f "$cs_out"
    elif [[ ! -f "$cs_out" ]]; then
        echo "FAIL (no .cs generated)"
        cs_failed=$((cs_failed + 1))
        cs_failures+=("$name")
    else
        mv "$cs_out" "$PROJECT_DIR/Example.cs"

        if dotnet build "$PROJECT_DIR/Verify.csproj" --no-restore --nologo -v quiet 2>/dev/null; then
            echo "OK"
            cp "$PROJECT_DIR/Example.cs" "$REPO_ROOT/examples/$name.cs"
            cs_passed=$((cs_passed + 1))
        else
            echo "FAIL (csc)"
            cs_failed=$((cs_failed + 1))
            cs_failures+=("$name")
        fi

        rm -f "$PROJECT_DIR/Example.cs"
    fi

    # --- IL backend ---
    echo -n "  $name (IL) ... "

    il_out="$PROJECT_DIR/$name.dll"
    if ! dotnet run --no-build --project "$REPO_ROOT/src/ZScript.Cli" -- \
        compile "$zs_file" --backend il --stdlib "$REPO_ROOT/src/ZScript.StdLib" \
        --module-path "$REPO_ROOT/src/ZScript.ZUnit" \
        --ref "$REF_DIR" \
        -o "$il_out" 2>/dev/null; then
        echo "FAIL (il compile)"
        il_failed=$((il_failed + 1))
        il_failures+=("$name")
    else
        echo "OK"
        il_passed=$((il_passed + 1))
    fi

    rm -f "$PROJECT_DIR/$name.dll" "$PROJECT_DIR/$name.exe"
done

total_cs=$((cs_passed + cs_failed))
total_il=$((il_passed + il_failed))
echo ""
echo "=== Results: $cs_passed/$total_cs C# passed, $il_passed/$total_il IL passed ==="
if [[ ${#cs_failures[@]} -gt 0 ]]; then
    echo "C# failures: ${cs_failures[*]}"
fi
if [[ ${#il_failures[@]} -gt 0 ]]; then
    echo "IL failures: ${il_failures[*]}"
fi
if [[ ${#cs_failures[@]} -gt 0 || ${#il_failures[@]} -gt 0 ]]; then
    exit 1
fi
