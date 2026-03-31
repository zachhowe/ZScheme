(namespace ZScheme.Examples)

(module importing-stdlib)

(import stdlib/math)

(define (sqrt2 [a : Double]) : Double
  (sqrt a))
