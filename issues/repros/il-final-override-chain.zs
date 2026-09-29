(namespace ZSchemeFuzzed)

(module f2repro)

(interface iint
  (M0 : Int))

(class #:open FCls_0 : iint
  [f0 : Int]
  (define (M0) : Int f0))

(class FCls_1 : FCls_0
  [d1_0 : Int]
  (define (M0) : Int (super/M0)))

(define (compute) : Int
  (begin (new FCls_1 5 2) 0))
