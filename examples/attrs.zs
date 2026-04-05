(namespace ZScheme.Examples)

(module attrs-example)

(import stdlib/attrs)

;; Mark a function for aggressive inlining
(with-method-impl aggressive-inlining
  (define (fast-add [x : Int] [y : Int]) : Int
    (+ x y)))

;; Mark a function as non-inlinable (useful for debugging or profiling)
(with-method-impl no-inlining
  (define (slow-multiply [x : Int] [y : Int]) : Int
    (* x y)))

;; Mark a function to skip optimization
(with-method-impl no-optimization
  (define (debug-divide [x : Int] [y : Int]) : Int
    (/ x y)))
