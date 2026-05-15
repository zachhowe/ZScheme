;; Result type — success or failure
;; Result<T, E> has two cases: (Ok value) and (Err error)

(namespace ZScheme.Examples)

(module results)

(import stdlib/result)
(import stdlib/error)

(define (safe-div [a : Int] [b : Int]) : (Result Int Error)
  (if (= b 0)
    (Err (make-error "division by zero"))
    (Ok (/ a b))))

(define (parse-digit [n : Int]) : (Result Int Error)
  (if (and (>= n 0) (<= n 9))
    (Ok n)
    (Err (make-error "value out of range"))))

(define (describe-result [r : (Result Int Error)]) : String
  (match r
    [(Ok v) (string-append "Success: " (int->string v))]
    [(Err e) "Failed"]))
