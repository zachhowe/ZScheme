(namespace ZScheme.Examples)

(module higher-order)

;; Lambdas and higher-order functions

;; Apply a function twice to a value
(define (apply-twice [f : (Int -> Int)] [x : Int]) : Int
  (f (f x)))

;; Return a closure that adds n to its argument
(define (make-adder [n : Int]) : (Int -> Int)
  (lambda (x) (+ n x)))

;; Compose two Int -> Int functions
(define (compose [f : (Int -> Int)] [g : (Int -> Int)]) : (Int -> Int)
  (lambda (x) (f (g x))))

;; Increment and double helpers
(define (inc [x : Int]) : Int (+ x 1))
(define (double [x : Int]) : Int (* x 2))

;; Usage: apply-twice inc 3  => 5
;; Usage: (make-adder 10) 5  => 15
;; Usage: (compose double inc) 3  => 8
