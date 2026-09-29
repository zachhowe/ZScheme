(namespace ZSchemeFuzzed)

(module f1repro)

(class #:open FCls_0
  [f0 : Int]
  (define (M0_0 [p0 : Int]) : Int (+ f0 p0))
  (define (M0_1 [p0 : Int]) : Int p0))

(class #:open FCls_1 : FCls_0
  [d1_0 : Int]
  (define (M0_0 [p0 : Int]) : Int (+ (super/M0_0 p0) d1_0)))

(class FCls_2 : FCls_1
  [d2_0 : Int]
  (define (M0_1 [p0 : Int]) : Int (super/M0_1 p0)))

(define (compute) : Int
  (begin (new FCls_2 1 2 3) 0))
