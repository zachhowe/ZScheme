# IL backend's EmitCall resolves module-level methods before locals, so a shadowing local is never called

## Symptom

Under the IL backend, a call to a local binding whose name matches a module-level function
calls the *function* instead of the local. The C# backend calls the local, so the two
backends disagree on the same source.

```scheme
(module e2e)
(import-clr [println System.Console/WriteLine])

(define-union Box (B [v : (Int -> Int)]))

(define (shadow-let [n : Int]) : Int
  (if (= n 0)
      0
      (let ([shadow-let (lambda ([x : Int]) : Int (* x 100))])
        (shadow-let (- n 1)))))

(define (shadow-match [b : Box]) : Int
  (match b
    [(B shadow-match) (shadow-match 1)]))

(define (main) : Unit
  (begin
    (println (int->string (shadow-let 3)))
    (println (int->string (shadow-match (B (lambda ([x : Int]) : Int (+ x 41))))))))
```

C# backend (correct — the local is called):

```
200
42
```

IL backend:

```
0
Unhandled exception. System.InvalidOperationException: Non-exhaustive match
   at ZSchemeGenerated.E2eModule.ShadowMatch(Box b)
   at ZSchemeGenerated.E2eModule.ShadowMatch(Box b)
   at ZSchemeGenerated.E2eModule.Main()
```

`shadow-let` recurses on the module function down to the base case and yields `0` instead of
`200`. `shadow-match` calls `ShadowMatch(1)` — the `Int` argument reinterpreted as a `Box` —
which then falls off the end of the match.

## Reproduce

```
$ zs compile e2e.zs -b il -o e2e_il
$ dotnet exec e2e_il.exe          # 0, then Non-exhaustive match
$ zs compile e2e.zs --emit-project -o e2e_proj && dotnet run --project e2e_proj
200
42
```

## Root cause

`IlEmitter.EmitCall` resolves a callee `IrNode.Var` in this order
(`src/ZScheme.Compiler/Codegen/IlEmitter.Emit.cs`):

1. `:2061` — defined module methods (`_methods`)
2. `:2139` — precompiled methods (`_precompiledMethods`)
3. `:2204` — locals
4. `:2215` — parameters
5. class fields, then static fields

Globals are consulted **before** locals and parameters, so a `let`/`match`-arm/parameter
binding that shadows a module-level function name can never win the lookup — the call is
emitted as a direct `Call` to the module method.

The non-call load path gets this right: `EmitLoadVar` (`:5040`) checks `locals` first
(`:5051`), then parameters, then static fields. So `(list/map shadow-let xs)` loads the
local while `(shadow-let 1)` calls the global — the same name resolving two different ways
in one method body. The C# backend has no equivalent inversion.

## Fix

Move the locals / parameters / class-field-delegate probes ahead of the `_methods` and
`_precompiledMethods` lookups in `EmitCall`, matching `EmitLoadVar`'s order and the C#
backend. Note the existing local/param branches additionally require the callee's type to be
`ZFuncType`/`ZDelegateType`; that guard is what keeps a same-named non-callable local from
swallowing a genuine call to the module function, so it must be preserved when reordering
rather than dropped.

Worth checking at the same time whether `_staticFields` (module-level values) needs the same
treatment relative to `_methods`.

## Priority note

Higher priority than the remaining backlog items: this is a **silent wrong-answer**
divergence between the two backends on ordinary source, not a missing diagnostic
(`package-builds-never-report-zs0005.md`, `tco-does-not-reach-class-and-object-methods.md`)
or unverifiable-but-working metadata
(`il-generic-erasure-produces-unverifiable-metadata.md`). It also escapes the differential
fuzzer today — `zs-fuzz` does not generate bindings that shadow a top-level function name, so
300 iterations of `compile,diffexec` pass clean against it. A generator that reuses function
names as `let`/pattern binders would catch this class.

## History

Found while fixing `tail-call-lowering-matches-self-calls-by-name-without-scope.md`, whose
repro used exactly this shadowing shape. That issue was a separate defect in the shared
`TailCallLowering` IR pass and is now fixed; this one is confirmed pre-existing and
independent — the IL output above is byte-for-byte the same behaviour on `28741f7` (before
that fix) as after it.
