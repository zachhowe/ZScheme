;; string-tests.zs — Tests for string utilities
(namespace ZScheme.StdLib.Tests)
(module string-tests)

(import zunit)
(import stdlib/string)

(test-suite StringTests
  (test-case format_no_placeholders
    (check-equal? "hello" (format "hello")))

  (test-case format_single_placeholder
    (check-equal? "hello world" (format "hello {0}" "world")))

  (test-case format_multiple_placeholders
    (check-equal? "a-b-c" (format "{0}-{1}-{2}" "a" "b" "c")))

  (test-case format_repeated_placeholder
    (check-equal? "ha ha" (format "{0} {0}" "ha")))

  (test-case format_with_int_conversion
    (check-equal? "count: 42" (format "count: {0}" (int->string 42))))

  (test-case format_empty_args
    (check-equal? "plain text" (format "plain text")))

  (test-case equals_same_strings
    (check-true (equals? "hello" "hello")))

  (test-case equals_different_strings
    (check-false (equals? "hello" "world")))

  (test-case equals_empty_strings
    (check-true (equals? "" "")))

  (test-case equals_empty_vs_nonempty
    (check-false (equals? "" "hello")))

  (test-case empty_true_for_empty_string
    (check-true (empty? "")))

  (test-case empty_false_for_nonempty_string
    (check-false (empty? "hello")))

  (test-case empty_false_for_whitespace
    (check-false (empty? " ")))

  (test-case starts_with_matching_prefix
    (check-true (starts-with? "hello world" "hello")))

  (test-case starts_with_non_matching_prefix
    (check-false (starts-with? "hello world" "world")))

  (test-case starts_with_empty_prefix
    (check-true (starts-with? "hello" "")))

  (test-case starts_with_full_string
    (check-true (starts-with? "hello" "hello")))

  (test-case ends_with_matching_suffix
    (check-true (ends-with? "hello world" "world")))

  (test-case ends_with_non_matching_suffix
    (check-false (ends-with? "hello world" "hello")))

  (test-case ends_with_empty_suffix
    (check-true (ends-with? "hello" "")))

  (test-case ends_with_full_string
    (check-true (ends-with? "hello" "hello"))))
