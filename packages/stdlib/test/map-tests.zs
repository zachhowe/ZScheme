;; map-tests.zs — Tests for Map operations
(namespace ZScript.StdLib.Tests)
(module map-tests)

(import zunit)
(import map)
(import option)
(import list)

(test-suite MapTests
  (test-case count_returns_size
    (check-equal? 2 (map/count (map-of ("a" 1) ("b" 2)))))

  (test-case put_adds_entry
    (check-equal? 3 (map/count (map/put (map-of ("a" 1) ("b" 2)) "c" 3))))

  (test-case remove_deletes_entry
    (check-equal? 1 (map/count (map/remove (map-of ("a" 1) ("b" 2)) "a"))))

  (test-case contains_key_true
    (check-true (map/contains-key? (map-of ("a" 1)) "a")))

  (test-case contains_key_false
    (check-false (map/contains-key? (map-of ("a" 1)) "b")))

  (test-case empty_on_empty_map
    (check-true (map/empty? (map-of))))

  (test-case empty_on_nonempty_map
    (check-false (map/empty? (map-of ("a" 1)))))

  (test-case get_returns_some_for_existing
    (check-true (option/some? (map/get (map-of ("a" 42)) "a"))))

  (test-case get_returns_none_for_missing
    (check-true (option/none? (map/get (map-of ("a" 42)) "b")))))
