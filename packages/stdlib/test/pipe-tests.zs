;; pipe-tests.zs — Tests for pipe macro
(namespace ZScheme.StdLib.Tests)
(module pipe-tests)

(import zunit)
(import stdlib/pipe)

;; Helper functions
(define (add [a : Int] [b : Int]) : Int (+ a b))
(define (mul [a : Int] [b : Int]) : Int (* a b))
(define (sub [a : Int] [b : Int]) : Int (- a b))
(define (double [x : Int]) : Int (* x 2))
(define (negate [x : Int]) : Int (- 0 x))
(define (inc [x : Int]) : Int (+ x 1))
(define (square [x : Int]) : Int (* x x))
(define (is-positive [x : Int]) : Bool (> x 0))

(test-suite PipeTests
  (test-case single_function_step
    (check-equal? 10 (|> 5 double)))

  (test-case single_apply_step
    (check-equal? 8 (|> 5 (add 3))))

  (test-case multiple_apply_steps
    (check-equal? 16 (|> 5 (add 1) (mul 3) (sub 2))))

  (test-case multiple_name_steps
    (check-equal? -7 (|> 3 double inc negate)))

  (test-case mixed_name_and_apply_steps
    (check-equal? 14 (|> 3 (add 2) double (add 4))))

  (test-case identity_pipe
    (check-equal? 42 (|> 42)))

  (test-case with_named_function_step
    (check-equal? 25 (|> 5 square)))

  (test-case type_changing_steps
    (check-true (|> 3 (add 1) is-positive)))

  (test-case nested_pipes
    (check-equal? 20 (|> (|> 3 (add 2)) (mul 4)))))
