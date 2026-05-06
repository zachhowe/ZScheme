;; result-tests.zs — Tests for Result type
(namespace ZScheme.StdLib.Tests)
(module result-tests)

(import zunit)
(import stdlib/result)
(import stdlib/error)

(test-suite ResultTests
  (test-case ok_wraps_value
    (check-equal? 42 (result/unwrap (Ok 42))))

  (test-case ok_is_ok
    (check-true (result/ok? (Ok 1))))

  (test-case err_is_err
    (check-true (result/err? (Err "bad"))))

  (test-case map_transforms_ok
    (check-equal? (Ok 10) (result/map (Ok 5) (lambda (x) (* x 2)))))

  (test-case map_preserves_err
    (check-true (result/err? (result/map (Err "fail") (lambda (x) (* x 2))))))

  (test-case flat_map_chains_ok
    (check-equal? (Ok 10)
      (result/flat-map (Ok 5) (lambda (x) (Ok (* x 2))))))

  (test-case flat_map_returns_err
    (check-true (result/err? (result/flat-map (Err "fail") (lambda (x) (Ok x)))))))
