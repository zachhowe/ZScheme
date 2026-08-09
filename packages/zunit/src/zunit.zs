;; zunit.zs — Rackunit-style testing assertions
(module zunit)

(import-clr
  Xunit
  [check-equal?     Assert/Equal ^a]
  [check-not-equal? Assert/NotEqual ^a]
  [check-true       Assert/True]
  [check-false      Assert/False]
  [fail             Assert/Fail])

(export check-equal? check-not-equal? check-true check-false
        check-pred check-not-false fail test-case test-suite test-suite-async
        theory-case inline-data test-case-async theory-case-async)

(define-syntax test-case
  (syntax-rules ()
    [(test-case name body ...)
     (begin (@ Xunit.FactAttribute) (define (name) (begin body ...)))]))

(define-syntax test-suite
  (syntax-rules (test-case)
    [(test-suite name (test-case tname tbody ...) ...)
     (define-class name
       (begin (@ Xunit.FactAttribute) (define (tname) : Unit (begin tbody ...))) ...)]))

(define-syntax theory-case
  (syntax-rules (inline-data)
    [(theory-case name (param ...) (inline-data d ...) ... body)
     (begin (@ Xunit.TheoryAttribute) (@ Xunit.InlineDataAttribute d ...) ... (define (name param ...) body))]))

(define-syntax test-case-async
  (syntax-rules ()
    [(test-case-async name body ...)
     (begin (@ Xunit.FactAttribute) (define-async (name) : Task (begin body ...)))]))

(define-syntax test-suite-async
  (syntax-rules (test-case-async)
    [(test-suite-async name (test-case-async tname tbody ...) ...)
     (define-class name
       (begin (@ Xunit.FactAttribute) (define-async (tname) : Task (begin tbody ...))) ...)]))

(define-syntax theory-case-async
  (syntax-rules (inline-data)
    [(theory-case-async name (param ...) (inline-data d ...) ... body)
     (begin (@ Xunit.TheoryAttribute) (@ Xunit.InlineDataAttribute d ...) ... (define-async (name param ...) : Task body))]))

;; Polymorphic check — uses generic Assert.Equal<T>
(define (check-not-false [v : Bool]) : Unit (check-true v))

;; Higher-order: call predicate, then assert true
(define (check-pred [pred : (^a -> Bool)] [v : ^a]) : Unit
  (check-true (pred v)))
