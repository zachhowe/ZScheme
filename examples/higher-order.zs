(namespace ZScheme.Examples)

(module higher-order)

;; Lambdas and higher-order functions

;; Apply a function twice to a value
(define (apply-twice [f : (Fn [Int] Int)] [x : Int]) : Int
  (f (f x)))

;; Return a closure that adds n to its argument
(define (make-adder [n : Int]) : (Fn [Int] Int)
  (fn [x] (+ n x)))

;; Compose two Int -> Int functions
(define (compose [f : (Fn [Int] Int)] [g : (Fn [Int] Int)]) : (Fn [Int] Int)
  (fn [x] (f (g x))))

;; Increment and double helpers
(define (inc [x : Int]) : Int (+ x 1))
(define (double [x : Int]) : Int (* x 2))

;; Usage: apply-twice inc 3  => 5
;; Usage: (make-adder 10) 5  => 15
;; Usage: (compose double inc) 3  => 8
