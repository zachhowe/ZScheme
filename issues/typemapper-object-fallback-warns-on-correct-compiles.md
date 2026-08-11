# `TypeMapper: Cannot map type ... falling back to object` warns on compiles that are correct

## Symptom

Compiling a perfectly good file with the IL backend prints a warning per erasure site,
naming user types that the compiler is in the middle of emitting:

```
$ dotnet run --project src/ZScheme.Cli -- compile /tmp/fsm2.zs -b il -o /tmp/fsm2.dll
Warning: TypeMapper: Cannot map type 'FsmSpec' to CLR type, falling back to object at (0:0)
Warning: TypeMapper: Cannot map type 'Ctx' to CLR type, falling back to object at (0:0)
Warning: TypeMapper: Cannot map type 'FsmResult' to CLR type, falling back to object at (0:0)
... (8 for this file)
Generated: /tmp/fsm2.dll
```

The output is correct — that assembly passes `ilverify` clean. The warnings are noise:
they have no span (`at (0:0)`), they name types the user declared perfectly correctly,
and there is no action the user can take about them.

## Root cause

`IlEmitter.MapToReflectionClr` resolves a `ZType` to a reflection `System.Type` through
`_userReflectionTypes`, which only ever holds types imported from a *precompiled*
assembly (`RegisterUserType` populates it solely when it is handed a `reflectionType`).
A record, union or class declared in the module being compiled has no loaded
`System.Type` at all, so every such lookup falls through to `TypeMapperCore`'s default
arm, which warns and returns `object`.

For the callers fixed in `a242836` that fallback was a real defect — it decided emitted
metadata. For the ~20 `MapToReflectionClr` call sites that remain, the result is used
only to *look a member up* or to answer "is this a value type?", where `object` is
harmless. Hence: correct output, spurious warning.

## Reproduce

Any file with a user type in a generic or higher-order position, e.g.

```scheme
(namespace St) (module st)
(import stdlib/treelist)
(define-struct Pt [x : Int] [y : Int])
(define pts (treelist (Pt 1 2) (Pt 3 4)))
(define (first-pt) : Pt (treelist-ref pts 0))
```

```
$ dotnet run --project src/ZScheme.Cli -- compile /tmp/st.zs -b il -o /tmp/st.dll
Warning: TypeMapper: Cannot map type 'Pt' to CLR type, falling back to object   (x6)
$ dotnet tool run ilverify -- /tmp/st.dll -r ... -s System.Private.CoreLib
All Classes and Methods in st.dll Verified.
```

## Suggested fix

Do **not** simply drop the warning — it is the only signal that surfaced the erasure bug
this issue was split out of. Two better options:

1. Give the reflection mapper an explicit "a locally-defined type is expected here and
   `object` is a fine stand-in" entry point, distinct from the general one, and warn only
   from the general one. That keeps a genuine unmappable-type warning loud while silencing
   the by-design fallback.
2. Or demote to `Log.Debug` and add an `ilverify` gate (the packages are already at zero
   errors) so regressions are caught by verification rather than by reading warnings.

Option 1 is preferable: it keeps the diagnostic pointed at the case it was written for.

## A standing risk worth recording

Every remaining `MapToReflectionClr` call site is a *latent* erasure of the kind
`a242836` fixed: if any of them ever starts feeding emitted metadata rather than a member
lookup, it silently produces the same unverifiable IL. Call sites are in
`IlEmitter.Emit.cs` (TCO-jump and call-argument box tests, `ClrNew` type args, tuple
match, receiver types, closure captures, delegate invoke) and `IlEmitter.Resolve.cs`.

I tried and failed to construct a currently-failing case from them — the probe above puts
a `define-struct` through generic calls, a hash, a treelist, `treelist-map` and a
capturing lambda, and all of it verifies. So this is recorded as a hazard, not a known
bug.

## Priority note

Lowest of the three open issues: cosmetic today. Worth doing before the ilverify gate
lands, because the whole point of that gate is that a fifteenth error should be visible,
and a compile that prints eight unactionable warnings is exactly the noise floor that
hides one.
