# IL backend types a module-level define holding a class instance as `object`

## Symptom

`ilverify` rejects `ZScheme.Examples.ObjectInheritanceModule::.cctor()` with three
`StackUnexpected` errors — one per `(object : Animal ...)` definition in the file:

```
[IL]: Error [StackUnexpected]: [... ObjectInheritanceModule::.cctor()]
  [offset 0x0000000F] [found ref 'object']
  [expected ref '[ZScheme.Examples]ZScheme.Examples.Animal'] Unexpected type on the stack.
  (also at 0x00000022 and 0x00000035)
```

The static field and the local that immediately reloads it are given *different* types.
Decoding the `.cctor` shows the pattern repeated three times:

```
0x0000  newobj  Void .ctor() on ZScheme.Examples.__Object_0     ; pushes __Object_0
0x0005  stsfld  System.Object Cat on ObjectInheritanceModule    ; field is object
0x000A  ldsfld  System.Object Cat on ObjectInheritanceModule    ; pushes object
0x000F  stloc.0                                                 ; local 0 is Animal  <-- error
```

Reflection over the emitted assembly confirms the split — the lifted object classes carry
the right base type, only the fields are wrong:

```
ObjectInheritanceModule.Cat     : System.Object      <-- should be Animal
ObjectInheritanceModule.LoudDog : System.Object
ObjectInheritanceModule.Parrot  : System.Object
ZScheme.Examples.__Object_0     base=ZScheme.Examples.Animal    (correct)
locals of .cctor: 0..2 : ZScheme.Examples.Animal                (correct)
```

The C# backend emits `public static Animal Cat = new __Object_0();` for the same source,
so this is an IL-backend-only divergence.

## Reproduce

```
$ dotnet run --project src/ZScheme.Cli -- compile examples/object-inheritance.zs \
    -b il -o /tmp/oi.dll
$ dotnet tool run ilverify -- /tmp/oi.dll \
    -r "$DOTNET/shared/Microsoft.NETCore.App/10.0.10/*.dll" \
    -r "/tmp/*.dll" -s System.Private.CoreLib
3 Error(s) Verifying oi.dll
```

Note there are **no** compiler warnings on this compile — the `TypeMapper: Cannot map
type ... falling back to object` warning does *not* fire here, so whatever erases the
field type is not `TypeMapperCore`'s default arm.

## It is not specific to `object` expressions

A four-line file separates the variable. A `define-record`-typed module field gets the
right type; a `define-class`-typed one does not, whether the value is an `(object ...)`
or a plain constructor call:

```scheme
(define-class #:open Animal [name : String] (define (Speak) : String name))
(define-record Box [v : String])

(define direct (Animal "cat"))                                   ;; field : System.Object
(define boxed  (Box "b"))                                        ;; field : Probe.ProbeModule+Box
(define anon   (object : Animal (constructor (super "Dog"))))    ;; field : System.Object
```

So the rule is "module-level `define` whose type is a `define-class`", not "object
expression". Records land as *nested* types (`ProbeModule+Box`) and classes as
*top-level* ones (`Probe.Animal`), which is the most visible difference between the two
cases and the obvious thing to check first.

## Where to look

The static-field definition path in `IlEmitter.Define.cs` versus the local-variable path
that `EmitLet`/`.cctor` emission uses — they disagree, so one of them is resolving
`Animal` through a registry that does not yet (or does not ever) contain top-level class
`TypeDefinition`s. Ordering is the leading hypothesis: if module static fields are
defined in a pass that runs before `define-class` types are registered in `_userTypes` /
`_userTypeSignatures`, `MapToClr` returns `object` for the field and the correct
signature later for the local. Confirm by logging the registry contents at each of the
two mapping calls before changing anything.

## Priority note

Higher priority than the other two open issues, and the same family as the
generic-erasure fix in `a242836` (commit "Keep user types out of the IL backend's
generic instantiations"): metadata that the runtime tolerates but `ilverify` rejects.
The example runs correctly today — adding a `main` that calls `Animal/Speak` on all
three objects prints the expected output — so the cost is unverifiable metadata, not a
wrong answer. But it is now the *only* real compiler defect left in the examples
corpus's ilverify output (the other 7 reported errors are `FileLoadErrorGeneric` for
`xunit.v3.assert`, an artifact of not passing the xunit assemblies to `-r`), so fixing
it puts the examples at zero and makes ilverify usable as a gate there, as it already
is for all eight packages.
