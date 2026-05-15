(namespace ZScheme.Examples)

(module error-handling)

(import stdlib/option)
(import stdlib/result)
(import stdlib/error)
(import stdlib/catch)

;; Error handling — structured errors,
;; .NET exception catching, and combining Option + Result.

;; Simple error
(define (simple-err) : Error
  (make-error "something went wrong"))

;; Convert Option to Result (None becomes an error)
(define (require [opt : (Option Int)] [msg : String]) : (Result Int Error)
  (match opt
    [(Some v) (Ok v)]
    [None (Err (make-error msg))]))

;; Catch .NET exceptions as Results
(import-clr
  [parse-int System.Int32/Parse])

(define (safe-parse [s : String]) : (Result Int Error)
  (catch (parse-int s)))

;; Combined pipeline: Option lookup -> Result -> validate
(define (find-user [name : String]) : (Option Int)
  (match name
    ["alice" (Some 42)]
    ["bob" (Some 7)]
    [_ None]))

(define (safe-div [a : Int] [b : Int]) : (Result Int Error)
  (if (= b 0)
    (Err (make-error "division by zero"))
    (Ok (/ a b))))
