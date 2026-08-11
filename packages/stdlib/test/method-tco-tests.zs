;; method-tco-tests.zs — A self-recursive method of a sealed class must run in constant stack.
;;
;; ZScheme has no `while`/`do`/named-`let`, so tail self-recursion is the only iteration the
;; language offers — and for a long time TailCallLowering descended only top-level functions,
;; which made the same body a loop or a stack-eater purely by where it was written. These
;; depths (200k) overflow if the method is emitted as plain recursion, so each case fails
;; loudly rather than silently regressing to recursion.
;;
;; The class is deliberately *not* `#:open`: an open class emits its methods virtual, so a
;; self-call has to dispatch to whatever a subclass overrides and the pass correctly leaves it
;; alone (ZS0005 `virtual` says so).
(namespace ZScheme.StdLib.Tests)
(module method-tco-tests)

(import zunit)

(define-union Peano (PZero) (PSucc [n : Peano]))

(define-class Looper
  [label : String]

  ;; The plain shape: a tail self-call in an `if` else-branch.
  (define (CountDown [n : Int] [acc : Int]) : Int
    (if (= n 0)
        acc
        (CountDown (- n 1) (+ acc 1))))

  ;; A `let` spine between the branch and the tail call — what a `begin` desugars to.
  (define (CountDownViaLet [n : Int] [acc : Int]) : Int
    (if (= n 0)
        acc
        (let ([next (- n 1)])
          (let ([total (+ acc 1)])
            (CountDownViaLet next total)))))

  ;; A back-edge from inside a `match` arm.
  (define (CountDownMatch [x : Peano] [acc : Int]) : Int
    (match x
      [(PZero) acc]
      [(PSucc m) (CountDownMatch m (+ acc 1))]))

  ;; Arguments that read the parameters the jump is about to overwrite: the back-edge must
  ;; stage them into temporaries first, or `b` reads the value `a` was just assigned.
  (define (Swap [a : Int] [b : Int] [n : Int]) : Int
    (if (= n 0)
        (- a b)
        (Swap b a (- n 1))))

  ;; Unit-returning: the loop's leaves produce nothing and must still terminate the method.
  (define (Spin [n : Int]) : Unit
    (if (= n 0)
        ()
        (Spin (- n 1))))

  ;; An awaited tail self-call in a method. This one's only await is the recursive call, so
  ;; TCO removes it entirely and no state machine is emitted — each leaf of the resulting
  ;; synchronous loop wraps its value back into a Task.
  (define-async (SpinAsync [n : Int] [acc : Int]) : (Task Int)
    (if (= n 0)
        acc
        (await (SpinAsync (- n 1) (+ acc 1)))))

  ;; ...and one that still awaits something else, so the loop lives inside the state
  ;; machine's MoveNext rather than replacing it.
  (define-async (BumpAsync [n : Int] [acc : Int]) : (Task Int)
    (if (= n 0)
        acc
        (let ([next (await (Bump acc))])
          (await (BumpAsync (- n 1) next)))))

  (define-async (Bump [x : Int]) : (Task Int)
    (+ x 1)))

(define (make-peano [n : Int] [acc : Peano]) : Peano
  (if (= n 0) acc (make-peano (- n 1) (PSucc acc))))

(test-suite MethodTcoTests
  (test-case deep_method_recursion_runs_in_constant_stack
    (check-equal? 200000 (Looper/CountDown (Looper "a") 200000 0)))

  (test-case deep_method_recursion_through_let_spine
    (check-equal? 200000 (Looper/CountDownViaLet (Looper "a") 200000 0)))

  (test-case deep_method_recursion_in_match_arm
    (check-equal? 200000
                  (Looper/CountDownMatch (Looper "a") (make-peano 200000 PZero) 0)))

  (test-case deep_method_recursion_returning_unit
    (begin
      (Looper/Spin (Looper "a") 200000)
      (check-true #t)))

  ;; An odd count leaves the arguments swapped, an even count restores them: only correct if
  ;; each jump reads the pre-jump values.
  (test-case back_edge_stages_arguments_before_assigning
    (begin
      (check-equal? -4 (Looper/Swap (Looper "a") 7 3 1))
      (check-equal? 4 (Looper/Swap (Looper "a") 7 3 2)))))

(test-suite-async MethodTcoAsyncTests
  (test-case-async deep_async_method_recursion_runs_in_constant_stack
    (let ([r (await (Looper/SpinAsync (Looper "a") 200000 0))])
      (check-equal? 200000 r)))

  (test-case-async deep_async_method_recursion_inside_state_machine
    (let ([r (await (Looper/BumpAsync (Looper "a") 200000 0))])
      (check-equal? 200000 r))))
