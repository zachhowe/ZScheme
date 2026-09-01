;; A ZScheme subtype check never walks past one edge, so nothing an interface or class
;; inherits *transitively* is visible to the type checker.
;;
;; `Unifier.IsZSchemeSubtype` compares the use site's interface against the class's
;; directly-declared interface list and returns false. The comment "Walk base class chain"
;; sits directly above that `return false` -- the walk was never written -- and no interface's
;; own `BaseInterfaceNames` is recorded anywhere for it to walk into.
;;
;; Every line below is legal at the CLR level: the emitted C# really is
;; `interface IDerived : IBase` and `class Impl : IDerived`, so `Impl` IS an `IBase`.
;;
;; Expected: compiles, returns 42. Actual: "Type mismatch: 'IBase' vs 'Impl'".
;; Swap in the commented line for the second, separate symptom:
;; "Undefined variable: 'IDerived-Go'" -- an inherited method gets no accessor.
(namespace ZSchemeRepro)
(module interface-inheritance-is-only-one-edge-deep)

(define-interface IBase (Go [] : Int))
(define-interface IDerived : IBase (Extra [] : Int))

(define-class Impl : IDerived
  (define (Go) : Int 2)
  (define (Extra) : Int 40))

(define (via-base [t : IBase]) : Int (IBase-Go t))

(define (compute) : Int
  (+ (via-base (Impl)) (IDerived-Extra (Impl))))
  ;; (+ (IDerived-Go (Impl)) (IDerived-Extra (Impl))))
