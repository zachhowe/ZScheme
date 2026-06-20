# Known C# Codegen Bugs (surfaced by the Roslyn compile-verification harness)

The `CSharpEmitterTests` harness now feeds every emitted module through Roslyn and fails
if it does not compile (errors only). Roslyn links against the full test-host dependency
closure (`TRUSTED_PLATFORM_ASSEMBLIES`), so missing-reference false positives are already
eliminated — **every bug below is the emitter producing C# that references a name existing
in no assembly, or otherwise invalid C#.**

These 23 tests are currently exempted from compile-verification via the
`KnownNonCompilingOutput` set in
`tests/ZScheme.Compiler.Tests/Codegen/CSharpEmitterTests.cs`. They still run their original
string assertions. When a root cause below is fixed, delete the corresponding entries from
that set; the harness will then guard those tests and fail loudly if the output still
doesn't compile.

Harness entry points:
- `tests/ZScheme.Compiler.Tests/Codegen/RoslynCompileVerifier.cs` — the Roslyn harness.
- `CSharpEmitterTests.Compile(...)` — wires verification in; skips names in `KnownNonCompilingOutput`.

---

## Group A — Type aliases left unresolved in inline-emitted stdlib bodies (13 tests)

**Symptom:** `CS0246: The type or namespace name 'TreeList<>' / 'List<>' / 'Vector<>' /
'Hash<,>' / 'MutableTreeList<>' / 'MutableHash<,>' could not be found`.

**Root cause:** ZScheme collection type aliases — declared with `define-type-alias`, e.g.
`(define-type-alias (TreeList ^a) System.Collections.Immutable.ImmutableList :from ...)` in
`packages/stdlib/src/treelist.zs` — are resolved in the *main* module's signatures but **not**
when imported stdlib modules are emitted inline (as `Stdlib_*Module` classes in the same
file). The inline bodies emit the raw alias name (`TreeList<T0>`) instead of the CLR target
(`System.Collections.Immutable.ImmutableList<T0>`). No CLR type named `TreeList`/`List`/
`Vector`/`Hash` exists, so it cannot compile.

Example (from `Emit_MutableList_UsesListClrType`, inside `Stdlib_Mutable_TreelistModule`):
```csharp
public static System.Collections.Generic.List<T0> TreelistCopy<T0>(TreeList<T0> xs)  // TreeList<T0> unresolved
```

**Likely fix area:** the type-alias registry / `TypeToCs` path must apply aliases when
lowering & emitting imported module definitions, not only the root module. See
`src/ZScheme.Compiler/Codegen/CSharpEmitter*.cs` and the `TypeAliasRegistry` plumbing in
`src/ZScheme.Compiler/Pipeline/Compilation.cs`.

Tests:
- `EmitBegin_DiscardingVoidReturningCall_EmitsAsStatement`
- `EmitClrNew_GenericType`
- `Emit_ConcurrentDictionary_UsesConcurrentDictionaryClrType`
- `Emit_FunctionParameterAlias_UsesClrTypeInSignature`
- `EmitFunctionWithBangSuffix_SanitizesIdentifier`
- `EmitGenericWithCollectionType`
- `Emit_Hash_UsesImmutableDictionaryClrType`
- `EmitLet_GenericCollectionValueWithFreeTypeVar_DefaultsToInt`
- `Emit_MutableHash_UsesDictionaryClrType`
- `Emit_MutableList_UsesListClrType`
- `Emit_NestedAliases_ResolvesAllLevels`
- `EmitVariadicCall_EmitsArrayConstruction`
- `EmitVariadicFunction_EmitsParamsKeyword`

---

## Group B — Object-expression / class-declaration emission (5 tests)

**B1 — interface declaration not emitted/qualified** (`CS0246: 'IComparer' / 'IFoo' / 'IBar'`):
the object expression implements an interface, and the emitted nested class plus the method
return type reference the interface name, but no `interface` declaration is emitted into the
file (and the name isn't namespace-qualified for a CLR interface).
```csharp
public static IComparer MakeComparer() { return new __Object_0(); }   // IComparer never declared
private sealed class __Object_0 : IComparer { ... }
```
- `EmitObjectExpr_SingleInterface`
- `EmitObjectExpr_MultipleInterfaces`

**B2 — base constructor arguments not forwarded** (`CS7036: no argument given for required
parameter 'Name' of '...Animal.Animal(...)'`): the object/derived class extends a base with a
required constructor parameter, but the emitted derived ctor doesn't pass it through.
- `EmitObjectExpr_WithBaseClass`
- `EmitObjectExpr_WithBaseClassAndInterface`

**B3 — duplicate member from inheritance** (`CS0102: type 'Base' already contains a definition
for 'Name'`): an inherited member is re-emitted on the derived/base type, producing a
duplicate definition.
- `EmitClassDecl_Inheritance_BaseClassAndInterface`

**Likely fix area:** object-expression and `define-class` emission in
`src/ZScheme.Compiler/Codegen/CSharpEmitter*.cs` (interface decl emission, base-ctor call
forwarding, inherited-member dedup).

---

## Group C — Method self/sibling calls emit the lowercase ZScheme name (2 tests)

**Symptom:** `CS0103: The name 'countdown' / 'double' does not exist in the current context`.

**Root cause:** a method that calls itself recursively or calls a sibling method emits the
original lowercase ZScheme identifier instead of the PascalCase C# method name (and without
the `this.`/type qualifier).
```csharp
public int Countdown(int n) { return ((n == 0) ? 0 : countdown((n - 1))); }  // should be Countdown
```
- `EmitClassMethod_RecursiveCall`
- `EmitClassMethod_CallsSiblingMethod`

---

## Group D — Non-generic `Task` value in statement position (2 tests)

**Symptom:** `CS0201: Only assignment, call, increment, decrement, await, and new object
expressions can be used as a statement`.

**Root cause:** an async expression whose value is a non-generic `Task` (or a `Task`-typed
binding) is emitted as a bare expression statement rather than being awaited / assigned /
discarded into a valid statement form.
- `EmitAsyncWithoutAwait_NonGenericTask`
- `EmitAwaitNonGenericTaskInLet`

---

## Group E — let-bound name out of scope in emitted body (1 test)

**Symptom:** `CS0103: The name 'y' does not exist in the current context`.

**Root cause:** a nested `let` whose body makes a CLR call emits a reference to a binding (`y`)
that isn't in scope at the emission site (lambda-lifting / scope plumbing for nested lets with
CLR-call bodies).
- `EmitNestedLetWithClrCallBody`
