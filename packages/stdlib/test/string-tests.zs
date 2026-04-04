;; string-tests.zs — Tests for string utilities
(namespace ZScheme.StdLib.Tests)
(module string-tests)

(import zunit)
(import stdlib/string)

(test-suite StringTests
  (test-case format_no_placeholders
    (check-equal? "hello" (string/format "hello")))

  (test-case format_single_placeholder
    (check-equal? "hello world" (string/format "hello {0}" "world")))

  (test-case format_multiple_placeholders
    (check-equal? "a-b-c" (string/format "{0}-{1}-{2}" "a" "b" "c")))

  (test-case format_repeated_placeholder
    (check-equal? "ha ha" (string/format "{0} {0}" "ha")))

  (test-case format_with_int_conversion
    (check-equal? "count: 42" (string/format "count: {0}" (int->string 42))))

  (test-case format_empty_args
    (check-equal? "plain text" (string/format "plain text")))

  (test-case equals_same_strings
    (check-true (string/equals? "hello" "hello")))

  (test-case equals_different_strings
    (check-false (string/equals? "hello" "world")))

  (test-case equals_empty_strings
    (check-true (string/equals? "" "")))

  (test-case equals_empty_vs_nonempty
    (check-false (string/equals? "" "hello")))

  (test-case empty_true_for_empty_string
    (check-true (string/empty? "")))

  (test-case empty_false_for_nonempty_string
    (check-false (string/empty? "hello")))

  (test-case empty_false_for_whitespace
    (check-false (string/empty? " ")))

  (test-case starts_with_matching_prefix
    (check-true (string/starts-with? "hello world" "hello")))

  (test-case starts_with_non_matching_prefix
    (check-false (string/starts-with? "hello world" "world")))

  (test-case starts_with_empty_prefix
    (check-true (string/starts-with? "hello" "")))

  (test-case starts_with_full_string
    (check-true (string/starts-with? "hello" "hello")))

  (test-case ends_with_matching_suffix
    (check-true (string/ends-with? "hello world" "world")))

  (test-case ends_with_non_matching_suffix
    (check-false (string/ends-with? "hello world" "hello")))

  (test-case ends_with_empty_suffix
    (check-true (string/ends-with? "hello" "")))

  (test-case ends_with_full_string
    (check-true (string/ends-with? "hello" "hello"))))
