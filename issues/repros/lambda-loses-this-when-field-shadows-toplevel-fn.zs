(namespace ZSchemeFuzzed)
(module g)
(import stdlib/treelist)

(define-class FCls_0
  [f0 : Int]
  (define (M0_0) : Int
    (treelist-length
      (treelist-filter (treelist 1 2) (lambda ([x : Int]) (> (+ x f0) 0))))))

(define (f0 [a : Int]) : Int a)

(define (compute) : Int (FCls_0/M0_0 (FCls_0 5)))
