# ClrTypeNames: `byte`/`Byte` type args map to System.UInt32 instead of System.Byte

**Found by:** direct unit-test coverage work (writing `ClrTypeNamesTests`), by
code inspection confirmed with a failing test.

**Affects:** `ClrTypeNames.ConvertTypeArg` and everything downstream of
`ClrTypeNames.ConvertToReflectionTypeName`: `TypeMapperCore.ResolveDelegateType`
(`Codegen/TypeMapperCore.cs:441`) and `ClrInterop.FindType`
(`Codegen/ClrInterop.cs:1342`). Any C#-style generic type name containing a
`byte` type argument — e.g. a delegate alias like `System.Func<byte,int>` or an
`import-clr` target `System.Collections.Generic.List<byte>` — resolves to the
**wrong CLR type** (`UInt32` in place of `Byte`) or fails to resolve.

Repro: run the skipped test

```
dotnet test tests/ZScheme.Compiler.Tests --filter "FullyQualifiedName~ClrTypeNamesTests.ByteMapsToSystemByte"
```

(remove the `Skip` from `ByteMapsToSystemByte` in
`tests/ZScheme.Compiler.Tests/Codegen/ClrTypeNamesTests.cs` first).

## Symptom

```csharp
ClrTypeNames.ConvertTypeArg("byte")  // returns "System.UInt32", expected "System.Byte"
ClrTypeNames.ConvertTypeArg("Byte")  // returns "System.UInt32", expected "System.Byte"
```

So `ConvertToReflectionTypeName("System.Func<byte,int>")` produces
``System.Func`2[System.UInt32,System.Int32]`` — a real, resolvable, and *wrong*
type. Because the result still resolves, this is a silent wrong-type hazard, not
a fail-loud one.

## Root cause

`src/ZScheme.Compiler/Codegen/ClrTypeNames.cs:58` lumps `byte` into the `uint`
arm of the switch:

```csharp
"byte" or "Byte" or "uint" or "UInt32" => "System.UInt32",
```

Almost certainly a copy/paste slip — the adjacent arms (`sbyte` → `System.SByte`,
`ushort` → `System.UInt16`) follow the correct pattern.

## Suggested fix direction

Split the arm:

```csharp
"byte" or "Byte" => "System.Byte",
"uint" or "UInt32" => "System.UInt32",
```

Then un-skip `ClrTypeNamesTests.ByteMapsToSystemByte`.

## Priority note

Trivial one-line fix, but a silent wrong-type mapping. Lower practical impact
than it sounds because `byte` type args in delegate/interop aliases are rare in
current ZScheme code, and the `ZType` pipeline maps `Byte` primitives through
`PrimitiveKind.Byte`, not through this string path. Sibling issue found in the
same file:
[clrtypenames-nested-generic-split.md](clrtypenames-nested-generic-split.md) —
fix together.
