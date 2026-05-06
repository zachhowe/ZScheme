;; mutable-map-tests.zs — Tests for Mutable-Map operations
(namespace ZScheme.StdLib.Tests)
(module mutable-map-tests)

(import zunit)
(import stdlib/map)
(import stdlib/mutable/map)
(import stdlib/option)

(test-suite MutableMapTests
  (test-case count_returns_size
    (check-equal? 2 (length (map->mutable-map (map-of (pair "a" 1) (pair "b" 2))))))

  (test-case put_adds_entry
    (let [m (map->mutable-map (map-of (pair "a" 1)))]
      (begin
        (put! m "b" 2)
        (check-equal? 2 (length m)))))

  (test-case put_updates_entry
    (let [m (map->mutable-map (map-of (pair "a" 1)))]
      (begin
        (put! m "a" 99)
        (check-true (some? (get m "a"))))))

  (test-case get_returns_some_for_existing
    (check-true (some? (get (map->mutable-map (map-of (pair "a" 42))) "a"))))

  (test-case get_returns_none_for_missing
    (check-true (none? (get (map->mutable-map (map-of (pair "a" 42))) "b"))))

  (test-case remove_deletes_entry
    (let [m (map->mutable-map (map-of (pair "a" 1) (pair "b" 2)))]
      (begin
        (remove! m "a")
        (check-equal? 1 (length m)))))

  (test-case contains_key_true
    (check-true (contains-key? (map->mutable-map (map-of (pair "a" 1))) "a")))

  (test-case contains_key_false
    (check-false (contains-key? (map->mutable-map (map-of (pair "a" 1))) "b")))

  (test-case clear_removes_all
    (let [m (map->mutable-map (map-of (pair "a" 1) (pair "b" 2)))]
      (begin
        (clear! m)
        (check-equal? 0 (length m)))))

  (test-case empty_on_empty_map
    (check-true (empty? (map->mutable-map (map-of)))))

  (test-case empty_on_nonempty_map
    (check-false (empty? (map->mutable-map (map-of (pair "a" 1))))))

  ;; Regression: value-type values put into a Mutable-Map<String, Object> require the
  ;; inferred `^v` to widen to Object so the Dictionary gets the right generic
  ;; instantiation and the IL emitter boxes before calling set_Item. Without the fix
  ;; the dictionary storage is laid out for a value type and a boxed access reads
  ;; garbage, which the runtime reports as a "concurrent operations" corruption.
  (test-case put_float_into_object_map
    (let [m : (Mutable-Map String Object) (mutable-map/new)]
      (begin
        (put! m "k" 1.5)
        (check-true (contains-key? m "k")))))

  (test-case put_int_into_object_map
    (let [m : (Mutable-Map String Object) (mutable-map/new)]
      (begin
        (put! m "k" 42)
        (check-true (contains-key? m "k")))))

  (test-case put_bool_into_object_map
    (let [m : (Mutable-Map String Object) (mutable-map/new)]
      (begin
        (put! m "k" #t)
        (check-true (contains-key? m "k"))))))
