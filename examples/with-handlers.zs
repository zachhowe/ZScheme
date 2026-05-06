(module with-handlers-demo)

(import-clr
  [string/format System.String/Format : (String String -> String)]
  [ex-message System.Exception.Message :instance-property : (System.Exception -> String)])

;; with-handlers.zs — Demonstrates the (with-handlers ...) special form
;; for catching specific .NET exception types

;; Basic: catch a specific exception type and return a default value
(define (safe-divide [a : Int] [b : Int]) : Int
  (with-handlers
    ([System.DivideByZeroException _] 0)
    (/ a b)))

;; Multiple handlers: catch different exception types with different responses
(define (categorize-error [a : Int] [b : Int]) : String
  (with-handlers
    ([System.DivideByZeroException _] "division by zero")
    ([System.OverflowException _] "overflow")
    (begin
      (/ a b)
      "ok")))

;; Using the bound exception variable to access the message
(define (describe-error [a : Int] [b : Int]) : String
  (with-handlers
    ([System.Exception e] (string/format "caught: {0}" (ex-message e)))
    (begin
      (/ a b)
      "ok")))

;; Combining with raise: throw and catch custom exceptions
(define (validate-positive [n : Int]) : Int
  (if (> n 0)
    n
    (raise (new System.ArgumentException "expected a positive number"))))

(define (try-validate [n : Int]) : Int
  (with-handlers
    ([System.ArgumentException _] 0)
    (validate-positive n)))
