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
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$RUNTIME_CSPROJ" />
  </ItemGroup>
</Project>
EOF

dotnet restore "$PROJECT_DIR/Verify.csproj" --nologo -v quiet

passed=0
failed=0
failures=()

for zs_file in "$REPO_ROOT"/examples/*.zs; do
    name="$(basename "$zs_file" .zs)"
    echo -n "  $name ... "

    # Compile .zs -> .cs
    cs_out="$PROJECT_DIR/$name.cs"
    if ! dotnet run --no-build --project "$REPO_ROOT/src/ZScript.Cli" -- \
        compile "$zs_file" --stdlib "$REPO_ROOT/src/ZScript.StdLib" \
        -o "$cs_out" 2>/dev/null; then
        echo "FAIL (zs compile)"
        failed=$((failed + 1))
        failures+=("$name")
        rm -f "$cs_out"
        continue
    fi

    if [[ ! -f "$cs_out" ]]; then
        echo "FAIL (no .cs generated)"
        failed=$((failed + 1))
        failures+=("$name")
        continue
    fi

    mv "$cs_out" "$PROJECT_DIR/Example.cs"

    # Verify C# compiles
    if dotnet build "$PROJECT_DIR/Verify.csproj" --no-restore --nologo -v quiet 2>/dev/null; then
        echo "OK"
        passed=$((passed + 1))
    else
        echo "FAIL (csc)"
        failed=$((failed + 1))
        failures+=("$name")
    fi

    rm -f "$PROJECT_DIR/Example.cs"
done

echo ""
echo "=== Results: $passed passed, $failed failed ==="
if [[ ${#failures[@]} -gt 0 ]]; then
    echo "Failures: ${failures[*]}"
    exit 1
fi
