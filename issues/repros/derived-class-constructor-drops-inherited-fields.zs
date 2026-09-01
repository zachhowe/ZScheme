;; A bare `(Derived args...)` constructor call drops every INHERITED field.
;;
;; The constructor's *type* is right -- TypeInferer.InferClassDecl builds it as
;; `inheritedFields ++ ownFields -> Derived`, so `(Derived 1)` type-checks -- and the
;; emitted constructor is right too: `Derived(int N) : base(N)`. Only the call site
;; disagrees: IrLowering registers the class under its OWN field names, then zips the
;; argument list against them. `Zip` truncates, so with zero own fields every argument
;; is silently discarded.
;;
;; Expected 11 (n = 1, + 10). The IL backend returns 10 -- `n` is left at its default.
;; The C# backend emits `new Derived()` against `Derived(int)`, which csc rejects (CS7036).
;;
;; `(new Derived 1)` on the same declarations is correct on both backends.
(namespace ZSchemeRepro)
(module derived-class-constructor-drops-inherited-fields)

(define-class #:open BaseThing
  [n : Int]
  (define (Value) : Int n))

;; No fields of its own, so the whole argument list is dropped.
(define-class Derived : BaseThing
  (define (Total) : Int (+ n 10)))

(define (compute) : Int (Derived-Total (Derived 1)))
