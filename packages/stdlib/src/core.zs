;; core.zs — Core combinators and utilities
(module core)

(export id const compose flip)

;; Identity function
(define (id [x : a]) : a x)

;; Constant function
(define (const [x : a] [y : b]) : a x)

;; Function composition (f . g)(x) = f(g(x))
(define (compose [f : (Fn [b] c)] [g : (Fn [a] b)] [x : a]) : c
  (f (g x)))

;; Flip argument order
(define (flip [f : (Fn [a b] c)] [x : b] [y : a]) : c
  (f y x))
