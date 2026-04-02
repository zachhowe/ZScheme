;; async-test-cases.zs — Demonstrates async test cases and async theory cases
(module async-test-cases)
(import zunit)

;; Async helper that returns a Task<Int>
(define-async (compute-async [x : Int]) : (Task Int)
  (+ x 1))

;; Async test case — awaits an async function and asserts the result
(test-case-async addition_async
  (let [result (await (compute-async 41))]
    (check-equal? 42 result)))

;; Async theory case — parameterized async test with multiple data rows
(theory-case-async compute_returns_incremented ([x : Int] [expected : Int])
  (inline-data 0 1)
  (inline-data 10 11)
  (inline-data -1 0)
  (inline-data 99 100)
  (let [result (await (compute-async x))]
    (check-equal? expected result)))
