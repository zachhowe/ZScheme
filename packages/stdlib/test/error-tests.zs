;; error-tests.zs — Tests for Error type
(namespace ZScheme.StdLib.Tests)
(module error-tests)

(import zunit)
(import stdlib/error)
(import stdlib/option)

(test-suite ErrorTests
  (test-case error_creates_error_info
    (let [e (Error "something failed")]
      (check-equal? "something failed" (ErrorInfo/message e))))

  (test-case error_has_no_cause
    (let [e (Error "test")]
      (check-true (none? (ErrorInfo/cause e))))))
