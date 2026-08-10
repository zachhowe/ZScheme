;; async-tco-tests.zs — Async self-recursion must run in constant stack.
;;
;; An async tail self-call can only be spelled `(await (self ...))`: a bare `(self ...)` has
;; type Task and will not unify with its sibling branch. TailCallLowering rewrites the whole
;; `await` into a back-edge, so these loops allocate one state machine instead of one per level.
;; Without that, each depth below overflows the stack.
;;
;; The helpers are deliberately top-level `define-async`: TailCallLowering only descends
;; top-level functions, so a loop written inside a `test-suite-async` body would be a class
;; method and would not be looped at all.
(namespace ZScheme.StdLib.Tests)
(module async-tco-tests)

(import zunit)

;; The plain shape: an awaited tail self-call in an `if` else-branch.
(define-async (count-down [n : Int] [acc : Int]) : (Task Int)
  (if (= n 0)
      acc
      (await (count-down (- n 1) (+ acc 1)))))

;; A `let` spine between the branch and the tail call — the shape a `begin` desugars to, and
;; the one ZWorld's merchant restock loop has.
(define-async (count-down-via-let [n : Int] [acc : Int]) : (Task Int)
  (if (= n 0)
      acc
      (let ([next (- n 1)])
        (let ([total (+ acc 1)])
          (await (count-down-via-let next total))))))

;; A back-edge from inside a `match` arm.
(define-async (count-down-match [n : Int] [acc : Int]) : (Task Int)
  (match n
    [0 acc]
    [m (await (count-down-match (- m 1) (+ acc 1)))]))

;; Unit-returning: TCO removes this loop's only await, so no state machine is emitted at all
;; and each leaf of the resulting synchronous loop has to wrap its value back into a Task.
(define-async (spin [n : Int]) : Task
  (if (= n 0)
      ()
      (await (spin (- n 1)))))

(test-suite-async AsyncTcoTests
  (test-case-async deep_async_recursion_runs_in_constant_stack
    (let ([r (await (count-down 200000 0))])
      (check-equal? 200000 r)))

  (test-case-async deep_async_recursion_through_let_spine
    (let ([r (await (count-down-via-let 200000 0))])
      (check-equal? 200000 r)))

  (test-case-async deep_async_recursion_in_match_arm
    (let ([r (await (count-down-match 200000 0))])
      (check-equal? 200000 r)))

  (test-case-async deep_async_recursion_returning_unit
    (begin
      (await (spin 200000))
      (check-true #t))))
