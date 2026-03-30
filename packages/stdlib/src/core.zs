;; core.zs — Core combinators and utilities
(module core)

(export id compose)

;; Identity function
(define (id [x : ^a]) : ^a x)

;; Function composition (f . g)(x) = f(g(x))
(define (compose [f : (Fn [^b] ^c)] [g : (Fn [^a] ^b)] [x : ^a]) : ^c
  (f (g x)))
