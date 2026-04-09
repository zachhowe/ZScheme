;; mutable-array-tests.zs — Tests for Mutable-Array operations
(namespace ZScheme.StdLib.Tests)
(module mutable-array-tests)

(import zunit)
(import stdlib/mutable/array)

(test-suite MutableArrayTests
  (test-case count_returns_length
    (check-equal? 3 (mutable-array/count (array->mutable-array (array 1 2 3)))))

  (test-case nth_returns_element
    (check-equal? 20 (mutable-array/nth (array->mutable-array (array 10 20 30)) 1)))

  (test-case set_replaces_element
    (let [xs (array->mutable-array (array 1 2 3))]
      (begin
        (mutable-array/set! xs 1 99)
        (check-equal? 99 (mutable-array/nth xs 1)))))

  (test-case empty_on_empty_array
    (check-true (mutable-array/empty? (array->mutable-array (array)))))

  (test-case empty_on_nonempty_array
    (check-false (mutable-array/empty? (array->mutable-array (array 1))))))
