;; The IL backend emits an UNLOADABLE class when the interface a class implements
;; inherits any method from a base interface.
;;
;; Every line here type-checks and both backends emit without a diagnostic. But the
;; IL backend marks only the DIRECTLY declared interface's methods as implementations:
;;
;;   Impl.Extra  Public, Final, Virtual, VtableLayoutMask   <- IDerived.Extra, correct
;;   Impl.Go     Public                                     <- implements nothing
;;
;; So IBase.Go is left unimplemented and the CLR refuses the type:
;;   TypeLoadException: Method 'Go' in type 'ZSchemeGenerated.Impl' ...
;;                      does not have an implementation.
;;
;; The C# backend emits both as Final, Virtual, NewSlot and loads fine — csc matches
;; implicit implementations against the whole interface set, transitively.
;;
;; Expected 42 from `compute`; the IL assembly throws on first touching `Impl`.
(namespace ZSchemeRepro)
(module il-class-does-not-implement-its-interfaces-inherited-methods)

(define-interface IBase (Go [] : Int))
(define-interface IDerived : IBase (Extra [] : Int))

(define-class Impl : IDerived
  (define (Go) : Int 2)
  (define (Extra) : Int 40))

;; Only spellings the type checker accepts today, so this compiles clean on both
;; backends -- see issues/interface-inheritance-is-only-one-edge-deep.md for the ones
;; it rejects.
(define (compute) : Int (+ (IDerived-Extra (Impl)) 2))
