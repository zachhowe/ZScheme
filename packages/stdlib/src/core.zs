;; core.zs — Core combinators and utilities
(module core)

(export id compose is-null?)

(import-clr
  [ref-equals System.Object/ReferenceEquals
    : (System.Object System.Object -> Bool)])

;; Identity function
(define (id [x : ^a]) : ^a x)

;; Null check — returns true if x is null
(define (is-null? [x : ^a]) : Bool
  (ref-equals x null))

;; Function composition (f . g)(x) = f(g(x))
(define (compose [f : (^b -> ^c)] [g : (^a -> ^b)] [x : ^a]) : ^c
  (f (g x)))
