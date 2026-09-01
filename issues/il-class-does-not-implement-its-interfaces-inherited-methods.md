# A class implementing an interface that inherits methods emits an unloadable type (IL backend)

**Found by:** hand-reduced while writing the type-name casing tests on `syntax-update`. Surfaced
while reflecting on the assembly for the front-end issue below — the program that *does*
type-check turns out to emit broken metadata. Reproduced against `ea33508`.

**Adjacent to [`interface-inheritance-is-only-one-edge-deep.md`](interface-inheritance-is-only-one-edge-deep.md)
but distinct.** That one is the type checker refusing legal programs, and it fails loudly. This
is the IL backend accepting a legal program and emitting an assembly the CLR will not load. They
share a shape — "the base-interface list is never walked" — but live in different layers, and
this half is the one that produces bad output.

**Affects:** the IL backend, for any `define-class` (or `object` expression, unverified) whose
declared interface *inherits* a method from a base interface. The C# backend is correct.

**No workaround** other than redeclaring the whole chain on the class —
`(define-class Impl : IDerived IBase …)` — which the type checker's own gap makes awkward.

## The defect

The class is emitted with the right interface list and the right methods, but only the
*directly declared* interface's methods are marked as implementations. From the repro assembly:

| method | attributes | implements |
|---|---|---|
| `Impl.Extra` | `Public, Final, Virtual, HideBySig, VtableLayoutMask` | `IDerived.Extra` ✅ |
| `Impl.Go` | `Public` | **nothing** ❌ |

`Go` is a plain instance method: no `Virtual`, no `NewSlot`, no `Final`. `IBase.Go` is therefore
unimplemented, and the CLR refuses the type on first touch.

For comparison, the same source through the **C# backend**:

| method | attributes |
|---|---|
| `Impl.Go` | `Public, Final, Virtual, HideBySig, VtableLayoutMask` |
| `Impl.Extra` | `Public, Final, Virtual, HideBySig, VtableLayoutMask` |

csc matches implicit implementations against the whole interface set transitively, so it gets
this right for free. That is the differential: two backends, same source, one loadable assembly
and one not.

## Repro

```
dotnet run --project src/ZScheme.Cli -- compile -b il \
  issues/repros/il-class-does-not-implement-its-interfaces-inherited-methods.zs
```

Compiles clean — no diagnostic from either backend. Loading the result:

```
System.TypeLoadException: Method 'Go' in type 'ZSchemeRepro.Impl' from assembly
'ZSchemeRepro, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null'
does not have an implementation.
```

```scheme
(define-interface IBase (Go [] : Int))
(define-interface IDerived : IBase (Extra [] : Int))

(define-class Impl : IDerived
  (define (Go) : Int 2)
  (define (Extra) : Int 40))

(define (compute) : Int (+ (IDerived-Extra (Impl)) 2))   ; expected 42
```

The body deliberately uses only spellings the type checker accepts today, so the program gets
all the way to a running assembly. Note the failure is not confined to calling `Go`: the type
cannot be loaded at all, so merely constructing `Impl` throws.

## Root cause

[`IlEmitter.cs:1695`](../src/ZScheme.Compiler/Codegen/IlEmitter.cs) builds the
`interfaceMethodNames` set that `EmitClass` consults when deciding whether to add
`NewSlot | Final` to a method. It walks base interfaces **for CLR interfaces only**:

```csharp
if (clrType is not null)
{
    foreach (var method in clrType.GetMethods())
        names.Add(method.Name);
    // Include methods from inherited interfaces
    foreach (var parentIface in clrType.GetInterfaces())
    foreach (var method in parentIface.GetMethods())
        names.Add(method.Name);
    return;
}

// Fall back to ZScheme-defined interfaces
if (!_userTypes.TryGetValue(ifaceName, out var userType) || userType is not TypeDefinition typeDef)
    return;

foreach (var method in typeDef.Methods)     // <- own methods only; no walk into typeDef.Interfaces
    if (method.Name is not null)
        names.Add(method.Name.ToString());
```

The CLR branch has the transitive walk and the ZScheme branch, ten lines below it, does not. The
fix is to recurse through `typeDef.Interfaces` in the fallback, mirroring what the branch above
already does — the interface's own base list is present in the emitted `TypeDefinition`
(`IlEmitter.Define.cs:55-59` writes it), so the data is there.

The same set drives the `NewSlot | Final` decision for property getters and setters
(`IlEmitter.Emit.cs:5841`, `:5873`), so a `#:mutable` field satisfying an inherited property
accessor has the identical hole.

## Not caught by `ilverify`

The metadata is structurally well-formed — `Impl` really does list `IDerived`, `IDerived` really
does list `IBase`, and every method body verifies. What is missing is a vtable slot, which is a
type-load check rather than a verification one.

Nor by any test: the existing interface coverage
(`examples/interfaces.zs`, `EndToEndTests`) only ever implements interfaces that declare all
their own methods. `examples/interfaces.zs` does declare `IAdvancedCalculator : ICalculator`,
but nothing in the file implements it.

## Priority note

The worst failure mode of the three found together. It compiles clean, verifies clean, and then
throws `TypeLoadException` on a type the user never sees a warning about — and it throws on
*construction*, so the stack trace points nowhere near the interface declaration that caused it.
It is also a silent backend divergence: the same source works through the C# backend, so a
package that passes `run-package-csharp-tests.ps1` can still fail under the IL backend.
