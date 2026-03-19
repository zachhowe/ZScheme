;; Result type — success or failure
;; Result<T, E> has two cases: (Ok value) and (Err error)
;; Use try/? for clean error propagation.

(namespace ZScript.Examples)

(module results)

(define (safe-div [a : Int] [b : Int]) : (Result Int Error)
  (if (= b 0)
    (Err (Error "division by zero"))
    (Ok (/ a b))))

(define (parse-digit [n : Int]) : (Result Int Error)
  (if (and (>= n 0) (<= n 9))
    (Ok n)
    (Err (Error "value out of range"))))

(define (describe-result [r : (Result Int Error)]) : String
  (match r
    [(Ok v) (string-append "Success: " (int->string v))]
    [(Err e) "Failed"]))

;; try/? — the ? operator unwraps Ok or returns Err early
(define (compute [a : Int] [b : Int] [c : Int]) : (Result Int Error)
  (try
    (let [x (? (safe-div a b))]
      (let [y (? (safe-div x c))]
        (Ok (+ x y))))))
