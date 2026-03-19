(namespace ZScript.Examples)

(module error-handling)

;; Error handling — structured errors,
;; .NET exception catching, and combining Option + Result.

;; Simple error
(define (simple-err) : Error
  (Error "something went wrong"))

;; Convert Option to Result (None becomes an error)
(define (require [opt : (Option Int)] [msg : String]) : (Result Int Error)
  (match opt
    [(Some v) (Ok v)]
    [None (Err (Error msg))]))

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
    (Err (Error "division by zero"))
    (Ok (/ a b))))

(define (user-score [name : String]) : (Result Int Error)
  (try
    (let [id (? (require (find-user name) "user not found"))]
      (let [score (? (safe-div (* id 100) (+ id 1)))]
        (Ok score)))))
