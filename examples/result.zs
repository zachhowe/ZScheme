;; Result type — success or failure
;; Result<T, E> has two cases: (Ok value) and (Err error)
;; Use try/? for clean error propagation.

(namespace ZScript.Examples)

(module results)

(import result)
(import error)

(define (safe-div [a : Int] [b : Int]) : (Result Int ErrorInfo)
  (if (= b 0)
    (Err (Error "division by zero"))
    (Ok (/ a b))))

(define (parse-digit [n : Int]) : (Result Int ErrorInfo)
  (if (and (>= n 0) (<= n 9))
    (Ok n)
    (Err (Error "value out of range"))))

(define (describe-result [r : (Result Int ErrorInfo)]) : String
  (match r
    [(Ok v) (string-append "Success: " (int->string v))]
    [(Err e) "Failed"]))

;; try/? — the ? operator unwraps Ok or returns Err early
(define (compute [a : Int] [b : Int] [c : Int]) : (Result Int ErrorInfo)
  (try
    (let [x (? (safe-div a b))]
      (let [y (? (safe-div x c))]
        (Ok (+ x y))))))
