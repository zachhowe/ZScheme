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

;; Left-to-right function composition — (compose f1 f2)(x) = f2(f1(x))
(define (compose [f1 : (^a -> ^b)] [f2 : (^b -> ^c)]) : (^a -> ^c)
  (lambda (x) (f2 (f1 x))))

;; Compose f1 and f2 and immediately apply to x — equivalent to ((compose f1 f2) x)
(define (compose/call [f1 : (^a -> ^b)] [f2 : (^b -> ^c)] [x : ^a]) : ^c
  (let ([nf (compose f1 f2)])
    (nf x)))
