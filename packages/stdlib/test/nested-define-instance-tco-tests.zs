;; nested-define-instance-tco-tests.zs — A loop helper written inside a method, using the
;; instance, must still run in constant stack.
;;
;; A nested `define` serving exactly one method is the natural way to write an accumulator loop
;; without widening the method's public signature. It reaches the instance by one of two routes,
;; and the two are chosen by what the group needs rather than by how it is written, so both
;; belong here: a field that cannot change after construction is captured by value and the group
;; lifts to a top-level static, while a `#:mutable` field, a sibling call or a `super/` call makes
;; the group a private method of the class instead.
;;
;; These depths (200k) overflow if either route stopped producing a loop, so each case fails
;; loudly rather than silently regressing to recursion. `Doubler` is deliberately `#:open`: its
;; own methods emit virtual and correctly do not loop, but the synthesized helper is private and
;; non-virtual and must — that is the one case where the class's own kind and the helper's differ.
(namespace ZScheme.StdLib.Tests)
(module nested-define-instance-tco-tests)

(import zunit)

(define-union Step (SDone) (SMore [rest : Step]))

(define-class Accum
  [step : Int]
  [label : String]

  ;; Immutable field, captured by value: the helper lifts to a static taking `step` as a
  ;; leading parameter, and the site reads it through `this` once.
  (define (SumBySteps [n : Int]) : Int
    (define (go [k : Int] [acc : Int]) : Int
      (if (= k 0) acc (go (- k 1) (+ acc step))))
    (go n 0))

  ;; The same, with a `let` spine between the branch and the tail call.
  (define (SumViaLet [n : Int]) : Int
    (define (go [k : Int] [acc : Int]) : Int
      (if (= k 0)
          acc
          (let ([next (- k 1)])
            (let ([total (+ acc step)])
              (go next total)))))
    (go n 0))

  ;; A back-edge out of a `match` arm, with the captured field read in the arm body.
  (define (SumViaMatch [s : Step] [acc : Int]) : Int
    (define (go [x : Step] [total : Int]) : Int
      (match x
        [(SDone) total]
        [(SMore rest) (go rest (+ total step))]))
    (go s acc))

  ;; Arguments that read the parameters the jump is about to overwrite: the back-edge has to
  ;; stage them into temporaries first, or `b` reads the value `a` was just assigned. The
  ;; captured field sits ahead of both in the parameter list, which is what makes the ordering
  ;; worth pinning here as well as for a plain function.
  (define (Swap [a : Int] [b : Int] [n : Int]) : Int
    (define (go [x : Int] [y : Int] [k : Int]) : Int
      (if (= k 0) (- x (+ y step)) (go y x (- k 1))))
    (go a b n))

  ;; Unit-returning: the loop's leaves produce nothing and must still terminate the helper.
  (define (Spin [n : Int]) : Unit
    (define (go [k : Int]) : Unit
      (if (= k 0) () (go (- k 1))))
    (go n))

  ;; A sibling method call from inside the loop. There is no by-value stand-in for this, so the
  ;; group becomes a private method and the call stays a real `this.Twice`.
  (define (Twice [n : Int]) : Int (* n 2))

  (define (SumDoubled [n : Int]) : Int
    (define (go [k : Int] [acc : Int]) : Int
      (if (= k 0) acc (go (- k 1) (+ acc (Twice step)))))
    (go n 0)))

;; A `#:mutable` field cannot be captured by value — that would freeze what the loop sees while
;; the source can still observe a write — so reading *or* writing one hosts the group on the
;; class. `#:open` on top of that: the helper must loop even though `Bump` itself does not.
(define-class #:open Doubler
  [total : Int #:mutable]

  (define (Bump [n : Int]) : Int
    (define (go [k : Int]) : Int
      (if (= k 0) total (begin (set! total (+ total 1)) (go (- k 1)))))
    (go n)))

(define (make-steps [n : Int] [acc : Step]) : Step
  (if (= n 0) acc (make-steps (- n 1) (SMore acc))))

(test-suite NestedDefineInstanceTcoTests
  ;; --- captured immutable field: the group lifts to a static ---
  (test-case deep_loop_reading_a_captured_field
    (check-equal? 200000 (Accum/SumBySteps (Accum 1 "a") 200000)))

  (test-case deep_loop_reading_a_captured_field_through_let_spine
    (check-equal? 200000 (Accum/SumViaLet (Accum 1 "a") 200000)))

  (test-case deep_loop_reading_a_captured_field_in_match_arm
    (check-equal? 200000
                  (Accum/SumViaMatch (Accum 1 "a") (make-steps 200000 SDone) 0)))

  (test-case deep_loop_returning_unit
    (begin
      (Accum/Spin (Accum 1 "a") 200000)
      (check-true #t)))

  ;; An odd count leaves the arguments swapped, an even count restores them: only correct if
  ;; each jump reads the pre-jump values rather than the ones it is assigning.
  (test-case back_edge_stages_arguments_before_assigning
    (begin
      (check-equal? -5 (Accum/Swap (Accum 1 "a") 7 3 1))
      (check-equal? 3 (Accum/Swap (Accum 1 "a") 7 3 2))))

  ;; --- needs a real `this`: the group becomes a private method ---
  (test-case deep_loop_calling_a_sibling_method
    (check-equal? 400000 (Accum/SumDoubled (Accum 1 "a") 200000)))

  (test-case deep_loop_reading_and_writing_a_mutable_field_on_an_open_class
    (check-equal? 200000 (Doubler/Bump (Doubler 0) 200000))))
