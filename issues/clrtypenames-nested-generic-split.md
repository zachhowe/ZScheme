# ClrTypeNames: nested generic type args are mangled by naive comma split

**Found by:** direct unit-test coverage work (writing `ClrTypeNamesTests`), by
code inspection confirmed with a failing test.

**Affects:** `ClrTypeNames.ConvertToReflectionTypeName` and its two consumers,
`TypeMapperCore.ResolveDelegateType` (`Codegen/TypeMapperCore.cs:441`) and
`ClrInterop.FindType` (`Codegen/ClrInterop.cs:1342`). Any C#-style generic name
whose type arguments are themselves generic — e.g. a delegate alias
`System.Func<System.Func<int,int>,int>` or an interop target
`System.Collections.Generic.List<System.Collections.Generic.List<int>>` — is
converted to a malformed reflection name, so `Type.GetType`/`Assembly.GetType`
returns null and the type silently fails to resolve.

Repro: run the skipped test

```
dotnet test tests/ZScheme.Compiler.Tests --filter "FullyQualifiedName~ClrTypeNamesTests.NestedGenericArgsAreConvertedRecursively"
```

(remove the `Skip` from `NestedGenericArgsAreConvertedRecursively` in
`tests/ZScheme.Compiler.Tests/Codegen/ClrTypeNamesTests.cs` first).

## Symptom

```csharp
ClrTypeNames.ConvertToReflectionTypeName("System.Func<System.Func<int,int>,int>")
// actual:   "System.Func`3[System.Func<int,int>,System.Int32]"
// expected: "System.Func`2[System.Func`2[System.Int32,System.Int32],System.Int32]"
```

Two distinct corruptions from the same cause:
1. **Wrong arity** — the outer type has 2 args but the split sees 3 comma-separated
   pieces (`System.Func<int`, `int>`, `int`), so the base becomes ``Func`3``.
2. **Unconverted args** — the fragments `System.Func<int` / `int>` don't match any
   `ConvertTypeArg` arm and pass through with raw angle brackets, which the
   reflection name grammar does not accept.

## Root cause

`src/ZScheme.Compiler/Codegen/ClrTypeNames.cs:31` and `:40` split the full
type-argument string on bare `','` without tracking angle-bracket depth:

```csharp
var arity = typeArgsStr.Split(',').Length;                       // line 31
var reflectedArgs = typeArgsStr.Split(',').Select(ConvertTypeArg)...; // line 40
```

Commas inside a nested `<...>` are argument separators of the *inner* type, but
the split treats them as top-level separators.

## Suggested fix direction

Replace the two `Split(',')` calls with a depth-aware splitter (increment on
`<`, decrement on `>`, split only at depth 0), then recurse: each top-level
fragment should go through `ConvertToReflectionTypeName` itself (so inner
generics get their own `` `N[...] `` form) before falling back to
`ConvertTypeArg` for simple names. The expected output above is parseable by
`Type.GetType` — nested reflection names need no extra bracket quoting as long
as they are not assembly-qualified.

## Priority note

Fail-quiet (type resolution returns null → downstream "type not found"
diagnostics) rather than a silent miscompile, and only reachable with nested
generic aliases, which are currently rare. Same file as
[clrtypenames-byte-maps-to-uint32.md](clrtypenames-byte-maps-to-uint32.md) —
fix together and un-skip both tests.
