(namespace ZScript.Examples)

(module let-star)

;; Sequential let bindings with let*
;; Each binding can reference all previous ones

;; Compute a quadratic: ax^2 + bx + c
(define (quadratic [a : Int] [b : Int] [c : Int] [x : Int]) : Int
  (let* ([x2 (* x x)]
         [ax2 (* a x2)]
         [bx (* b x)])
    (+ (+ ax2 bx) c)))

;; Step-by-step transformation pipeline using let*
(define (transform [n : Int]) : Int
  (let* ([doubled (* n 2)]
         [incremented (+ doubled 1)]
         [squared (* incremented incremented)])
    squared))

;; Shadowing: later bindings can redefine earlier names
(define (accumulate [x : Int]) : Int
  (let* ([x (+ x 1)]
         [x (* x 2)]
         [x (+ x 10)])
    x))
