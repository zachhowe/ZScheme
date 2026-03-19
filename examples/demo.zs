(namespace ZScript.Examples)

(module demo)

;; GCD using Euclidean algorithm
(define (gcd [a : Int] [b : Int]) : Int
  (if (= b 0) a (gcd b (% a b))))

;; Fibonacci (tail-recursive)
(define (fib [n : Int] [a : Int] [b : Int]) : Int
  (if (= n 0) a (fib (- n 1) b (+ a b))))

;; Simple arithmetic
(define (square [x : Int]) : Int (* x x))

;; Nested let
(define (compute [x : Int]) : Int
  (let [a (+ x 1)]
    (let [b (* a 2)]
      (+ a b))))
