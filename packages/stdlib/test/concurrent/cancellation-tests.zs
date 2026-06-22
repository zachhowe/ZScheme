;; cancellation-tests.zs — Tests for cancellation-source/token operations
(namespace ZScheme.StdLib.Tests)
(module cancellation-tests)

(import zunit)
(import stdlib/concurrent/cancellation)

(test-suite CancellationTests
  (test-case new_source_not_requested
    (check-false (cancellation/requested? (cancellation/new))))

  (test-case cancel_sets_requested
    (let ([src (cancellation/new)])
      (begin
        (cancellation/cancel! src)
        (check-true (cancellation/requested? src)))))

  (test-case token_reflects_source_cancel
    (let ([src (cancellation/new)])
      (let ([token (cancellation/token src)])
        (begin
          (check-false (cancellation/token-requested? token))
          (cancellation/cancel! src)
          (check-true (cancellation/token-requested? token))))))

  (test-case none_token_not_requested
    (check-false (cancellation/token-requested? (cancellation/none))))

  (test-case new_with_timeout_not_immediately_requested
    (check-false (cancellation/requested? (cancellation/new-with-timeout 100000))))

  (test-case dispose_after_use
    (let ([src (cancellation/new)])
      (begin
        (cancellation/dispose! src)
        (check-true #t)))))
