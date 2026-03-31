;; mutable-map-tests.zs — Tests for Mutable-Map operations
(namespace ZScript.StdLib.Tests)
(module mutable-map-tests)

(import zunit)
(import stdlib/mutable-map)
(import stdlib/option)

(test-suite MutableMapTests
  (test-case count_returns_size
    (check-equal? 2 (mutable-map/count (map->mutable-map (map-of ("a" 1) ("b" 2))))))

  (test-case put_adds_entry
    (let [m (map->mutable-map (map-of ("a" 1)))]
      (begin
        (mutable-map/put! m "b" 2)
        (check-equal? 2 (mutable-map/count m)))))

  (test-case put_updates_entry
    (let [m (map->mutable-map (map-of ("a" 1)))]
      (begin
        (mutable-map/put! m "a" 99)
        (check-true (option/some? (mutable-map/get m "a"))))))

  (test-case get_returns_some_for_existing
    (check-true (option/some? (mutable-map/get (map->mutable-map (map-of ("a" 42))) "a"))))

  (test-case get_returns_none_for_missing
    (check-true (option/none? (mutable-map/get (map->mutable-map (map-of ("a" 42))) "b"))))

  (test-case remove_deletes_entry
    (let [m (map->mutable-map (map-of ("a" 1) ("b" 2)))]
      (begin
        (mutable-map/remove! m "a")
        (check-equal? 1 (mutable-map/count m)))))

  (test-case contains_key_true
    (check-true (mutable-map/contains-key? (map->mutable-map (map-of ("a" 1))) "a")))

  (test-case contains_key_false
    (check-false (mutable-map/contains-key? (map->mutable-map (map-of ("a" 1))) "b")))

  (test-case clear_removes_all
    (let [m (map->mutable-map (map-of ("a" 1) ("b" 2)))]
      (begin
        (mutable-map/clear! m)
        (check-equal? 0 (mutable-map/count m)))))

  (test-case empty_on_empty_map
    (check-true (mutable-map/empty? (map->mutable-map (map-of)))))

  (test-case empty_on_nonempty_map
    (check-false (mutable-map/empty? (map->mutable-map (map-of ("a" 1)))))))
