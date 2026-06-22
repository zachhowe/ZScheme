;; error-tests.zs — Tests for Error type
(namespace ZScheme.StdLib.Tests)
(module error-tests)

(import zunit)
(import stdlib/error)
(import stdlib/option)

(test-suite ErrorTests
  (test-case error_creates_error
    (let ([e (make-error "something failed")])
      (check-equal? "something failed" (Error/message e))))

  (test-case error_has_no_inner
    (let ([e (make-error "test")])
      (check-true (none? (Error/inner e))))))
