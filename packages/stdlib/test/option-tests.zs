;; option-tests.zs — Tests for Option type
(namespace ZScheme.StdLib.Tests)
(module option-tests)

(import zunit)
(import stdlib/option)

(test-suite OptionTests
  (test-case some_wraps_value
    (check-equal? 42 (option/unwrap (Some 42))))

  (test-case none_is_none
    (check-true (option/none? None)))

  (test-case some_is_some
    (check-true (option/some? (Some 1))))

  (test-case unwrap_or_returns_value_when_some
    (check-equal? 5 (option/unwrap-or (Some 5) 0)))

  (test-case unwrap_or_returns_default_when_none
    (check-equal? 0 (option/unwrap-or None 0)))

  (test-case map_transforms_some
    (check-equal? (Some 10) (option/map (Some 5) (fn [x] (* x 2)))))

  (test-case map_preserves_none
    (check-true (option/none? (option/map None (fn [x] (* x 2))))))

  (test-case flat_map_chains_some
    (check-equal? (Some 10)
      (option/flat-map (Some 5) (fn [x] (Some (* x 2))))))

  (test-case flat_map_returns_none
    (check-true (option/none? (option/flat-map None (fn [x] (Some x)))))))
