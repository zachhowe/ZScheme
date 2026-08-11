; The *value* (not call) form of the same collision, and it breaks BOTH
; backends: `f0` passed as a function value inside a constructor's `super`
; argument, where the class also has an `f0` field.
;
;   C#  -> emits `Apply1(F0, 5)`, which Roslyn rejects:
;          CS0120: An object reference is required for the non-static field,
;          method, or property 'Fieldfn_valueModule.Derived.F0'
;   IL  -> `ldarg.0; ldfld int32 Derived::F0` where a Func`3 is expected;
;          ilverify StackUnexpected, and InvalidProgramException at runtime.

(namespace ZSchemeFuzzed)

(module fieldfn_value)

(define-class #:open Base
  [b0 : Int #:mutable]
  (define (M) : Int b0))

(define (f0 [a : Int] [b : Int]) : Int (+ a b))

(define (apply1 [g : (Int Int -> Int)] [x : Int]) : Int (g x x))

(define-class Derived : Base
  [f0 : Int #:mutable]
  (constructor [p : Int]
    (super (apply1 f0 5))
    (set! f0 p))
  (define (M) : Int f0))

(define (compute) : Int
  (begin (new Derived 3) 0))
