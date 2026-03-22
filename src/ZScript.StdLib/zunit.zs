;; zunit.zs — Rackunit-style testing assertions
(module zunit)

(import-clr Xunit)
(import-clr
  [assert-equal     Xunit.Assert/Equal ^a]
  [assert-not-equal Xunit.Assert/NotEqual ^a]
  [assert-true      Xunit.Assert/True]
  [assert-false     Xunit.Assert/False]
  [assert-fail      Xunit.Assert/Fail])

(export check-equal? check-not-equal? check-true check-false
        check-pred check-not-false fail test-case test-suite)

(define-syntax test-case
  (syntax-rules ()
    [(test-case name body ...)
     (begin (@ Xunit.FactAttribute) (define (name) (begin body ...)))]))

(define-syntax test-suite
  (syntax-rules (test-case)
    [(test-suite name (test-case tname tbody ...) ...)
     (class name
       (begin (@ Xunit.FactAttribute) (tname [] : Unit (begin tbody ...))) ...)]))

;; Polymorphic check — uses generic Assert.Equal<T>
(define (check-equal? [expected : ^a] [actual : ^a]) : Unit
  (assert-equal expected actual))

(define (check-not-equal? [expected : ^a] [actual : ^a]) : Unit
  (assert-not-equal expected actual))

(define (check-true [v : Bool]) : Unit (assert-true v))
(define (check-false [v : Bool]) : Unit (assert-false v))
(define (check-not-false [v : Bool]) : Unit (assert-true v))

;; Higher-order: call predicate, then assert true
(define (check-pred [pred : (Fn [a] Bool)] [v : a]) : Unit
  (assert-true (pred v)))

(define (fail [msg : String]) : Unit (assert-fail msg))
