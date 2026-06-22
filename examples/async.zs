;; async.zs — Demonstrates async/await support
(module async)

;; Define an async function that returns Task<Int>
(define-async (compute-async [x : Int]) : (Task Int)
  (+ x 1))

;; Define an async function that awaits another async call
(define-async (fetch-and-add [x : Int]) : (Task Int)
  (let ([result (await (compute-async x))])
    (+ result 10)))

;; Nested await inside let bindings
(define-async (double-compute [x : Int]) : (Task Int)
  (let ([a (await (compute-async x))])
    (let ([b (await (compute-async a))])
      (+ a b))))

;; Async function that returns non-generic Task (no return value)
(define-async (do-work) : Task
  (await (compute-async 42)))
