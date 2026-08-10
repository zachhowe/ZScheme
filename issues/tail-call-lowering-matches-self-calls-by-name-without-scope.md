# TailCallLowering matches self-calls by bare name, miscompiling shadowed names and polymorphic recursion

## Symptom

The pass decides "this is a self-call" from the callee's name alone:

```
src/ZScheme.Compiler/Ir/TailCallLowering.cs:93
    case IrNode.Call { Function: IrNode.Var v } call when v.Name == funcName:
```

No scope tracking and no type-argument comparison, which gives two wrong answers:

1. **A local that shadows the function's own name.** A tail call to that local is rewritten
   into a back-edge, so the program jumps to the top of the enclosing function instead of
   calling the value the user bound. `(define (f n) (let ([f 1]) (g f)))` is the documented
   shape.
2. **Polymorphic recursion.** `f<T>` calling `f<int>` is a call to a *different*
   instantiation; a name-based jump reuses the current one.

Both are known and were left in deliberately when the pass was written — the code comment at
`TailCallLowering.cs:96-100` names them, `docs/COMPILER-PIPELINE.md` repeats them, and
`TailRecursionDriftTests.ShadowedSelfName_IsNotCoveredByTheDriftContract` pins the shadowing
case as a documented divergence rather than asserting correct behaviour:

```
    // TailCallLowering matches Var.Name with no scope tracking, so it wrongly rewrites a
    // call to a shadowing local as a back-edge. The analyzer is scope-aware and correctly
    // stays silent. Documented here rather than "fixed" in the analyzer to match the bug.
```

Filed so it is tracked as a defect rather than only as a comment: this is a silent
wrong-code path, and the test currently locks in the wrong behaviour.

## Notes for a fix

- The analyzer already does the scope-aware half correctly (`Walk`'s `shadowed` parameter,
  threaded through `Let`/`Use`/`Lambda`/`Match` binders). Whatever the pass uses should agree
  with it, or the drift biconditional becomes a lie in the other direction.
- The IR-level fix is probably not "add scope tracking to the pass" but "mark the self-call
  upstream" — `IrLowering` knows which `Var` resolved to the enclosing function, and a
  resolved marker would fix the generic case at the same time by carrying the instantiation.
  That is also the machinery
  `issues/tco-does-not-reach-class-and-object-methods.md` needs, so the two are worth
  designing together.
- When it is fixed, `ShadowedSelfName_IsNotCoveredByTheDriftContract` should flip from
  documenting the divergence to asserting the pass leaves the shadowed call alone.

## Priority

Low in practice — shadowing a function's own name with a value and then tail-calling it is
rare, and ZScheme's generics do not commonly recurse polymorphically. But the failure is
silent and produces wrong answers rather than an error, which is the worst shape a compiler
bug can have.
