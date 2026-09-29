(module minrepro3)

(interface IFuz_0
  (M0_0 [p0 : Float] : Int))

(class #:open FCls_0 : IFuz_0
  [f0 : Int]
  (define (M0_0 [p0 : Float]) : Int f0))

(class #:open fCls_1 : FCls_0
  [d1_0 : Int]
  (define (M0_0 [p0 : Float]) : Int (+ (super/M0_0 p0) d1_0)))

(class fCls_2 : fCls_1
  [d2_0 : Int]
  (define (M0_0 [p0 : Float]) : Int (super/M0_0 p0)))

(define (compute) : Int
  (begin (new fCls_2 1 2 3) 0))
