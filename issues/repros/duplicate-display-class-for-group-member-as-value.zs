(namespace ZSchemeFuzzed)
(module n)

(define (h [f : (Int -> Int)] [n : Int]) : Int (f n))

(define (compute) : Int
  (letrec ([x96 (lambda ([n : Int]) : Int (if (<= n 0) 0 (x96 (- n 1))))])
    (+ (h x96 1) (h x96 2))))
