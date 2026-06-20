# Known C# Codegen Bugs (surfaced by the Roslyn compile-verification harness)

The `CSharpEmitterTests` harness now feeds every emitted module through Roslyn and fails
if it does not compile (errors only). Roslyn links against the full test-host dependency
closure (`TRUSTED_PLATFORM_ASSEMBLIES`), so missing-reference false positives are already
eliminated — **every bug below is the emitter producing C# that references a name existing
in no assembly, or otherwise invalid C#.**

These 10 tests are currently exempted from compile-verification via the
`KnownNonCompilingOutput` set in
`tests/ZScheme.Compiler.Tests/Codegen/CSharpEmitterTests.cs`. They still run their original
string assertions. When a root cause below is fixed, delete the corresponding entries from
that set; the harness will then guard those tests and fail loudly if the output still
doesn't compile.

Harness entry points:
- `tests/ZScheme.Compiler.Tests/Codegen/RoslynCompileVerifier.cs` — the Roslyn harness.
- `CSharpEmitterTests.Compile(...)` — wires verification in; skips names in `KnownNonCompilingOutput`.

---

## Group A — FIXED (was: type aliases unresolved in inline-emitted stdlib bodies)

The original writeup blamed the emitter for "not applying aliases when imported modules are
emitted inline." That was wrong: inline-emitted module bodies and the main module use the
*same* `TypeToCs` / `_typeAliases` path. The real defect was that the referenced alias was
never **registered** in the compilation-wide `TypeAliasRegistry`, because the module that
*declares* it was not in the import graph.

Several stdlib collection modules referenced each other's aliases without importing the
declaring module — and couldn't, because the references are circular (`treelist` ↔
`mutable/treelist`, `vector` ↔ `mutable/vector`, `hash` ↔ `mutable/hash`). Real builds never
hit this because they route through `list.zs` / the prelude, which imports every collection
module together; the tests import a single module with the prelude disabled, exposing the gap.
The precompiled-library path was never affected — `LibraryCompiler.BuildAliasRegistry`
compiles the whole stdlib together, so all aliases are collected before emission.

Fixed in the stdlib source by re-declaring each cross-referenced alias locally in the modules
that use it (the duplicate-target diagnostic guards consistency), plus correcting a genuine
type bug in `concurrent/dictionary.zs` (`keys`/`values` were typed against the `List` union
but produce an `ImmutableList`, i.e. `TreeList`). No compiler change.

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
