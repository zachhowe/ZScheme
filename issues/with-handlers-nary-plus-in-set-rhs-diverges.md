# A with-handlers inside an n-ary `+` inside a fold lambda used as a struct `set!` RHS diverges

**Found by:** the fuzzer (2026-09-29 fuzz session, case `6cdb2e5c`; shrinked
by delta-debugging). None of the ingredients are new — with-handlers, n-ary
`+`, inline fold lambdas and struct `set!` were all generated before — but the
combination only surfaced once the generator's RNG stream shifted.

**Affects:** both backends disagree on the computed value. IL returns the
with-handlers *try body's* value (as if the surrounding sum collapsed to the
handler form); C# returns the correct sum.

## Symptom

Repro: [`repros/with-handlers-nary-plus-set-rhs.zs`](repros/with-handlers-nary-plus-set-rhs.zs)

```scheme
(struct SRec_1 [f0 : Int] [f1 : Int] [f2 : Int #:mutable])

(define (compute) : Int
  (let ([x408 (SRec_1 0 0 0)])
    (begin (set! x408 f2 (treelist-fold (treelist 0 50 0 0) 0
             (lambda ([x492 : Int] [x493 : Int])
               (+ 5 (with-handlers ([System.InvalidOperationException x499] (% x492 19)) 0)
                  0 0 0))))
      (SRec_1-f2 x408))))
```

`[il-run] returned 0`, C# returns `5`. Both backends compile; ilverify passes.

Removing any ingredient makes the divergence disappear: the with-handlers
hoisted to a top-level function (`m21`), the `set!` replaced by a plain `let`
(`m17`), the `+` reduced to two operands, the lambda lifted to a top-level
define, or the whole expression moved out of the `set!` RHS.

## Root cause

Not yet diagnosed. The shape crosses three lowerings that each rewrite the
expression tree — `WithHandlersHoister` (hoists the handler to top level and
rewrites captures), the n-ary `+` desugar (left-folds to binary), and the
`set!` RHS materialization for a value-type field — so the likely culprit is
an ordering or splicing interaction between them (a rewritten node left in
place, or the fold lambda's body replaced by the hoisted form's try value).

## Suggested fix direction

Compile the repro with both backends and diff the IR before/after
`WithHandlersHoister` and the `+` desugar; check what expression ends up in
the `set!` RHS on the IL side.
