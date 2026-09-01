# Interface inheritance is only one edge deep

**Found by:** hand-reduced while writing the type-name casing tests on `syntax-update` — the
`define-interface … : i-base` fixture I wanted would not type-check. Reproduced against
`ea33508`.

**Affects:** the type checker, for every use of a ZScheme interface that needs to look past a
single declared edge: a class used as one of its interface's *base* interfaces, a subclass used
as an interface its *base class* declares, one interface widened to another, and any accessor
for a method an interface *inherits*. Casing-independent.

**See also [`il-class-does-not-implement-its-interfaces-inherited-methods.md`](il-class-does-not-implement-its-interfaces-inherited-methods.md)**
— the same "the base-interface list is never walked" shape in the IL emitter, which is the half
that produces bad output. This one fails loudly and miscompiles nothing.

**No workaround** beyond declaring every interface in the chain explicitly on every class
(`(define-class Impl : IDerived IBase …)`), which does not help the widening cases at all.

## The defect

`Unifier.IsZSchemeSubtype(className, interfaceName)` compares the use site's interface against
the class's **directly declared** interface list and then gives up. The comment describing the
missing half is still there, sitting directly above the `return false` —
[`Unifier.cs:549-566`](../src/ZScheme.Compiler/Types/Unifier.cs):

```csharp
private bool IsZSchemeSubtype(string className, string interfaceName)
{
    var interfaces = classInterfaceLookup!(className);
    if (interfaces is null)
        return false;

    var target = _canonical(interfaceName);
    foreach (var declared in interfaces)
        if (declared == interfaceName || _canonical(declared) == target)
            return true;

    // Walk base class chain
    // classInterfaceLookup returns null for unknown classes, so this terminates
    return false;
}
```

The walk was never written. Two things would have to feed it:

- **A class's base chain.** `TypeInferer.InferClassDecl` already walks it for fields and methods
  (`GetAllInheritedFields` / `GetAllInheritedMethods`) but builds `effectiveInterfaceNames` from
  `node.InterfaceNames` alone, so a subclass inherits its base's fields and methods but not its
  interfaces.
- **An interface's own base list.** `TypeInferer.InferInterfaceDecl` ignores
  `node.BaseInterfaceNames` completely — it is never recorded anywhere, so even a written walk
  would have nothing to walk into.

That second gap has its own separate symptom: because `InferInterfaceDecl` also registers
accessors only for `node.Methods`, an inherited method gets none.

## Repro

```
dotnet run --project src/ZScheme.Cli -- compile \
  issues/repros/interface-inheritance-is-only-one-edge-deep.zs
```

```scheme
(define-interface IBase (Go [] : Int))
(define-interface IDerived : IBase (Extra [] : Int))

(define-class Impl : IDerived
  (define (Go) : Int 2)
  (define (Extra) : Int 40))

(define (via-base [t : IBase]) : Int (IBase-Go t))
(define (compute) : Int (+ (via-base (Impl)) (IDerived-Extra (Impl))))
```

```
Error: Type mismatch: 'IBase' vs 'Impl'
```

## What works and what does not

All against `ea33508`, all on the same declarations.

| shape | result |
|---|---|
| `(IDerived-Extra (Impl))` — method declared *on* `IDerived` | pass |
| `Impl` passed where `IDerived` is expected | pass |
| `(IDerived-Go (Impl))` — method `IDerived` *inherits* from `IBase` | **`Undefined variable: 'IDerived-Go'`** |
| `Impl` passed where `IBase` is expected | **`Type mismatch: 'IBase' vs 'Impl'`** |
| `IDerived` value passed where `IBase` is expected | **`Type mismatch: 'IBase' vs 'IDerived'`** |
| `Sub : Base` where `Base : IFoo`, `Sub` passed where `IFoo` is expected | **`Type mismatch: 'IFoo' vs 'Sub'`** |

So one declared edge resolves and nothing beyond it does.

## The declarations themselves are emitted correctly

Compiling only the rows that pass — which is enough to get a full assembly out — shows the
declaration side is already right. Both backends emit:

```csharp
public interface IBase { int Go(); }
public interface IDerived : IBase { int Extra(); }
public sealed class Impl : IDerived { … }
```

At the CLR level `Impl` **is** an `IBase`, so every failing row above is a program the runtime
would accept. The checker just cannot see the relation.

(The IL backend gets the *declarations* right and the vtable wrong — that is the separate issue
linked at the top. Do not read "emitted correctly" here as "the IL output works".)

## Priority note

Fails loudly and early, so nothing miscompiles — but it makes interface hierarchies unusable
past depth one, which is the point of having them. The last row is the most surprising in
practice: a subclass silently loses the interfaces its base class declares, so `#:open` class +
interface is a combination that cannot be extended.

The two halves are separable. Recording `BaseInterfaceNames` in `InferInterfaceDecl` and
registering accessors for inherited methods fixes the `IDerived-Go` row on its own; the
remaining four rows need the transitive closure in `IsZSchemeSubtype`, which wants that
recording in place first. Doing the recording also puts the data where the IL emitter's fix
could share it.

## Why nothing caught it

`examples/interfaces.zs` declares `IAdvancedCalculator : ICalculator` — and stops there. Nothing
in the file, or in `EndToEndTests`, implements an interface that inherits anything, so no test
exercises a second edge in either direction.
