(namespace ZSchemeFuzzed)

(module f3brepro)

(class fCls_0
  [f0 : Int]
  [f1 : Int]
  (constructor [a0 : Int]
    (set! f0 5)
    (set! f1 a0))
  (define (M) : Int f0))

(define (compute) : Int
  (begin (new fCls_0 5) 0))
