;; hash-tests.zs — Tests for Hash operations
(namespace ZScheme.StdLib.Tests)
(module hash-tests)

(import zunit)
(import stdlib/hash)
(import stdlib/option)
(import stdlib/treelist)

(test-suite HashTests
  (test-case count_returns_size
    (check-equal? 2 (hash-count (hash (pair "a" 1) (pair "b" 2)))))

  (test-case put_adds_entry
    (check-equal? 3 (hash-count (hash-set (hash (pair "a" 1) (pair "b" 2)) "c" 3))))

  (test-case remove_deletes_entry
    (check-equal? 1 (hash-count (hash-remove (hash (pair "a" 1) (pair "b" 2)) "a"))))

  (test-case contains_key_true
    (check-true (hash-has-key? (hash (pair "a" 1)) "a")))

  (test-case contains_key_false
    (check-false (hash-has-key? (hash (pair "a" 1)) "b")))

  (test-case empty_on_empty_map
    (check-true (hash-empty? (hash))))

  (test-case empty_on_nonempty_map
    (check-false (hash-empty? (hash (pair "a" 1)))))

  (test-case get_returns_some_for_existing
    (check-true (some? (hash-ref (hash (pair "a" 42)) "a"))))

  (test-case get_returns_none_for_missing
    (check-true (none? (hash-ref (hash (pair "a" 42)) "b")))))
