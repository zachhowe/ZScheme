(namespace ZSchemeFuzzed)
(module l)
(import stdlib/treelist)

(define-class FCls_0
  [f0 : Int #:mutable]
  (define (M0_1) : Int
    (define (x78 [n : Int]) : Int (if (<= n 0) f0 (x78 (- n 1))))
    (treelist-length
      (treelist-filter (treelist 1 2) (lambda ([x : Int]) (> (x78 x) 0))))))

(define (compute) : Int (FCls_0/M0_1 (FCls_0 5)))
