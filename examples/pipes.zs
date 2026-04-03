;; Pipe operator and partial application

(namespace ZScheme.Examples)

(module pipes)

(import stdlib/pipe)

;; Arithmetic helpers
(define (add [a : Int] [b : Int]) : Int (+ a b))
(define (mul [a : Int] [b : Int]) : Int (* a b))
(define (sub [a : Int] [b : Int]) : Int (- a b))

;; Chain operations with |>
(define (pipeline-demo [x : Int]) : Int
  (|> x (add 1) (mul 3) (sub 2)))

;; Partial application: create specialized functions
(define add5 (partial add 5))
(define double (partial mul 2))

;; Combine partial application with pipes
(define (transform [x : Int]) : Int
  (|> x add5 double))
