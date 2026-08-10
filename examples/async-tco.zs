;; async-tco.zs — Async self-recursion compiles to a loop, not a stack of state machines.
;;
;; ZScheme has no `while`/`do`/`for`/named-`let`: tail self-recursion is the only iteration the
;; language offers, and TailCallLowering is what makes it constant-stack. An *async* tail
;; self-call can only be written `(await (self ...))`, because a bare `(self ...)` has type Task
;; and will not unify with its sibling branch — so the pass matches that whole `await` and
;; rewrites it to a back-edge. The IL backend consumes it inside the state machine's MoveNext;
;; the C# backend emits `continue`. Either way one state machine, one builder, one Task for the
;; whole loop instead of one per level.
(module async-tco)

;; The plain shape: awaited tail self-call in an `if` else-branch.
(define-async (count-down [n : Int] [acc : Int]) : (Task Int)
  (if (= n 0)
      acc
      (await (count-down (- n 1) (+ acc 1)))))

;; A `let` spine between the branch and the tail call — what a `begin` desugars to.
(define-async (sum-to [n : Int] [acc : Int]) : (Task Int)
  (if (= n 0)
      acc
      (let ([next (- n 1)])
        (await (sum-to next (+ acc n))))))

;; A back-edge from inside a `match` arm.
(define-async (count-down-match [n : Int] [acc : Int]) : (Task Int)
  (match n
    [0 acc]
    [m (await (count-down-match (- m 1) (+ acc 1)))]))

;; Unit-returning: this loop's only await is the recursive one, so TCO removes it entirely and
;; no state machine is emitted — the leaves just wrap their value back into a Task.
(define-async (spin [n : Int]) : Task
  (if (= n 0)
      ()
      (await (spin (- n 1)))))

;; Not looped, and correctly so: the awaited result feeds the `+`, so the frame has to survive
;; until the callee returns. This one still recurses on the stack, and ZS0005 says so.
(define-async #:recursive (sum-non-tail [n : Int]) : (Task Int)
  (if (= n 0)
      0
      (+ n (await (sum-non-tail (- n 1))))))

(define-async (main) : Task
  (begin
    (await (count-down 1000000 0))
    (await (sum-to 1000000 0))
    (await (count-down-match 1000000 0))
    (await (spin 1000000))
    (await (sum-non-tail 100))
    ()))
