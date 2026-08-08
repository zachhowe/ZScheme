# IL backend: class-method call to an imported module function fails when the exporting module is emitted later

## Symptom

`Function 'shared-combat-on-damage!' not found for AsmResolver IL emission`
(`IlEmitter.Emit.cs:2182`) when a `define-class` method body calls a
module-level function imported from another module — but only in
compilations where the exporting module happens to be emitted *after* the
module containing the class.

Hit in ZWorld (`run/scripts`): `behaviors/guard.zs` defines `GuardBehavior`
whose `OnDamageTakenAsync` awaits `(shared-combat-on-damage! Ctx evt)`
imported from `lib/shared-states.zs`. The main package build succeeds, but
the per-test-file compilation of `guard-behavior-tests.zs` (which imports
`lib/fsm`, `lib/behavior-helper`, `behaviors/guard`) fails with the error
above at the call site in guard.zs.

## Root cause

Two interacting behaviors:

1. **Module emission order follows import-discovery preorder, not a
   dependency-first topological order.** In the failing compile the order was
   `fsm → bandit → …abilities… → shared-states → … → guard`: `bandit` (the
   test file's direct import) was emitted before its own dependency
   `shared-states`, which was only discovered through it. Debug log excerpt:

   ```
   Pass 0a complete for ZworldScripts_Lib_FsmModule: 0 let bindings, 6 functions
   EmitCall: looking up variable 'shared-combat-on-damage!' (ModuleName=zworld-scripts/lib/shared-states, sanitized=SharedCombatOnDamage_b, qualifiedKey=ZworldScripts_Lib_SharedStatesModule.SharedCombatOnDamage_b)
   Pass 0a complete for ZworldScripts_Behaviors_BanditModule: 0 let bindings, 7 functions   <-- lookup failed here
   ...
   RegisterFuncSignature: registered 'SharedCombatOnDamage_b' ... for function shared-combat-on-damage!
   Pass 0a complete for ZworldScripts_Lib_SharedStatesModule: 0 let bindings, 34 functions  <-- too late
   ```

2. **Pass 0a emits `define-class` method bodies eagerly**, while plain
   module-level functions are only signature-registered
   (`RegisterFuncSignature`) as each module's pass 0a runs. So a class method
   body emitted in pass 0a can only call imported functions whose module's
   pass 0a already ran. Module-level function bodies don't hit this because
   they're emitted in a later pass, after all signatures exist.

The same call from a module-level function compiles fine regardless of
order; only class-method call sites are order-sensitive.

## Repro sketch

Three modules compiled as one unit where the *root* imports `b` before `a`
is discovered:

```scheme
;; a.zs
(module a)
(define (a-helper [x : Int]) : Int (+ x 1))
(export a-helper)

;; b.zs — imports a, defines a class calling a-helper in a method
(module b)
(import mypkg/a)
(define-interface IThing (Poke [x : Int] : Int))
(define-class Thing : IThing
  (constructor)
  (define (Poke [x : Int]) : Int (a-helper x)))
(export Thing)

;; root.zs
(module root)
(import mypkg/b)   ;; b discovered (and emitted) before a
```

If the emission order places `b` before `a`, IL emission of `Thing.Poke`
fails with `Function 'a-helper' not found for AsmResolver IL emission`.

## Suggested fix

Register **all** modules' function signatures (the `RegisterFuncSignature`
step of pass 0a) across the whole compilation before emitting any bodies —
or at minimum before emitting any `define-class` method bodies. Alternatively
make the emission order a true dependency-first topological sort, but the
two-phase signature registration is the more robust fix (it also covers
cycles-via-classes if those ever become legal).

## Workaround (in use in ZWorld)

Import the exporting library directly in the root/test file *before* the
module whose class methods call it, so preorder discovery emits the library
first:

```scheme
(import zworld-scripts/lib/shared-states)  ;; before the behavior import
(import zworld-scripts/behaviors/guard)
```
