;; zunit.zs — Rackunit-style testing assertions
(module zunit)

(import-clr
  [assert-equal-int   ZScript.ZUnit.ZsAssert/EqualInt]
  [assert-equal-bool  ZScript.ZUnit.ZsAssert/EqualBool]
  [assert-equal-str   ZScript.ZUnit.ZsAssert/EqualStr]
  [assert-equal-obj   ZScript.ZUnit.ZsAssert/EqualObj]
  [assert-not-equal   ZScript.ZUnit.ZsAssert/NotEqualObj]
  [assert-true        ZScript.ZUnit.ZsAssert/IsTrue]
  [assert-false       ZScript.ZUnit.ZsAssert/IsFalse]
  [assert-fail        ZScript.ZUnit.ZsAssert/Fail])

(define-syntax test-case
  (syntax-rules ()
    [(test-case name body ...)
     (begin (@ Xunit.FactAttribute) (define (name) (begin body ...)))]))

(export check-equal? check-not-equal? check-true check-false
        check-pred check-not-false fail test-case)

;; Polymorphic check — uses object equality (works for boxed primitives + records)
(define (check-equal? [expected : System.Object] [actual : System.Object]) : Unit
  (assert-equal-obj expected actual))

(define (check-not-equal? [expected : System.Object] [actual : System.Object]) : Unit
  (assert-not-equal expected actual))

(define (check-true [v : Bool]) : Unit (assert-true v))
(define (check-false [v : Bool]) : Unit (assert-false v))
(define (check-not-false [v : Bool]) : Unit (assert-true v))

;; Higher-order: call predicate, then assert true
(define (check-pred [pred : (Fn [a] Bool)] [v : a]) : Unit
  (assert-true (pred v)))

(define (fail [msg : String]) : Unit (assert-fail msg))
