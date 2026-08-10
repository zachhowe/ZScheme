# Tail-call optimization never reaches class/object methods, and ZS0005 stays quiet about it

## Symptom

A self-recursive method on a `define-class` or `object` is emitted as plain recursion, no
matter how it is written, and no diagnostic says so. The same body as a top-level `define`
becomes a loop.

`TailCallLowering.Rewrite` only descends two node kinds:

```
src/ZScheme.Compiler/Ir/TailCallLowering.cs:56-68
    return node switch
    {
        IrNode.Seq seq  => …,
        IrNode.FuncDef  => RewriteFunc(func),
        _ => node,                       // <-- ClassDecl and its methods land here
    };
```

`TailRecursionAnalyzer` deliberately abstains on method bodies, so nothing warns:

```
src/ZScheme.Compiler/Types/TailRecursionAnalyzer.cs:24-27
    Class/object methods and constructors are skipped — a bare `(foo x)` in a
    method body does not resolve to the method, so treating it as a self-call would
    be a false positive.
```

That abstention is correct as written — but it means the one place a user gets told "this
consumes stack" is silent precisely where the pass also does nothing, so a method is
un-looped *and* unreported.

## Why it is hard rather than just unwired

The analyzer comment names the real obstacle: at AST level a bare `(foo x)` inside a method
body does not resolve to the enclosing method. There is no self-reference notion for methods
to match on, which is why the pass matches `IrNode.Call { Function: IrNode.Var }` by name and
why extending that naively to methods would rewrite unrelated calls into back-edges.

Any fix needs a resolved "this is a call to the method we are inside" marker, produced
somewhere that knows the class's scope, plus the corresponding loop emission in both
backends' method-body paths (`IlEmitter`'s instance-method emission and
`CSharpEmitter.EmitInstanceMethodBody`, neither of which currently look at `IsTcoLoop`).

## Impact

Bounded today, but it is a real language-level asymmetry: the same code loops or does not
purely by where it is written, and ZScheme has no `while`/`do`/named-`let` to fall back on,
so a method has *no* constant-stack iteration available at all.

Known affected code in a downstream consumer — ZWorld's `AddNItems`
(`run/scripts/src/abilities/mining-ability.zs:60-69`), an async class method counting down
`(- n 1)`. Bounded by a mining node's output quantity, so harmless in practice, but it is
recursion where the author almost certainly expected a loop.

Also worth noting for anyone writing package tests: a `test-suite-async` body compiles to
class methods, so a loop written inside one is silently not a loop. Test helpers that need to
prove constant-stack behaviour must be top-level `define`/`define-async`
(see `packages/stdlib/test/async-tco-tests.zs`, which says so in its header for this reason).

## Interim mitigation

None needed for correctness — lift the loop to a top-level `define` and call it from the
method. Cheap and obvious once you know; the problem is that nothing tells you.

If a fix is not near-term, the cheaper half is worth doing alone: teach the analyzer to
recognise a method self-call well enough to warn (`not-top-level` already exists as a reason
and would fit), so the silence becomes a warning even while the pass stays out.
