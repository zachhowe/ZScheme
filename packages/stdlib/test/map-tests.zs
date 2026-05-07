;; map-tests.zs — Tests for Map operations
(namespace ZScheme.StdLib.Tests)
(module map-tests)

(import zunit)
(import stdlib/map)
(import stdlib/option)
(import stdlib/treelist)

(test-suite MapTests
  (test-case count_returns_size
    (check-equal? 2 (length (map-of (pair "a" 1) (pair "b" 2)))))

  (test-case put_adds_entry
    (check-equal? 3 (length (put (map-of (pair "a" 1) (pair "b" 2)) "c" 3))))

  (test-case remove_deletes_entry
    (check-equal? 1 (length (remove (map-of (pair "a" 1) (pair "b" 2)) "a"))))

  (test-case contains_key_true
    (check-true (contains-key? (map-of (pair "a" 1)) "a")))

  (test-case contains_key_false
    (check-false (contains-key? (map-of (pair "a" 1)) "b")))

  (test-case empty_on_empty_map
    (check-true (empty? (map-of))))

  (test-case empty_on_nonempty_map
    (check-false (empty? (map-of (pair "a" 1)))))

  (test-case get_returns_some_for_existing
    (check-true (some? (get (map-of (pair "a" 42)) "a"))))

  (test-case get_returns_none_for_missing
    (check-true (none? (get (map-of (pair "a" 42)) "b")))))
