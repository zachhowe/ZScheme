;; string-tests.zs — Tests for string utilities
(namespace ZScript.StdLib.Tests)
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
    (check-equal? "plain text" (string/format "plain text"))))
