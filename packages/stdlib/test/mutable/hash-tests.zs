;; mutable-hash-tests.zs — Tests for Mutable-Hash operations
(namespace ZScheme.StdLib.Tests)
(module mutable-hash-tests)

(import zunit)
(import stdlib/hash)
(import stdlib/mutable/hash)
(import stdlib/option)

(test-suite MutableHashTests
  (test-case count_returns_size
    (check-equal? 2 (hash-count (hash-copy (hash (pair "a" 1) (pair "b" 2))))))

  (test-case put_adds_entry
    (let ([m (hash-copy (hash (pair "a" 1)))])
      (begin
        (hash-set! m "b" 2)
        (check-equal? 2 (hash-count m)))))

  (test-case put_updates_entry
    (let ([m (hash-copy (hash (pair "a" 1)))])
      (begin
        (hash-set! m "a" 99)
        (check-true (some? (hash-ref m "a"))))))

  (test-case get_returns_some_for_existing
    (check-true (some? (hash-ref (hash-copy (hash (pair "a" 42))) "a"))))

  (test-case get_returns_none_for_missing
    (check-true (none? (hash-ref (hash-copy (hash (pair "a" 42))) "b"))))

  (test-case remove_deletes_entry
    (let ([m (hash-copy (hash (pair "a" 1) (pair "b" 2)))])
      (begin
        (hash-remove! m "a")
        (check-equal? 1 (hash-count m)))))

  (test-case contains_key_true
    (check-true (hash-has-key? (hash-copy (hash (pair "a" 1))) "a")))

  (test-case contains_key_false
    (check-false (hash-has-key? (hash-copy (hash (pair "a" 1))) "b")))

  (test-case clear_removes_all
    (let ([m (hash-copy (hash (pair "a" 1) (pair "b" 2)))])
      (begin
        (hash-clear! m)
        (check-equal? 0 (hash-count m)))))

  (test-case empty_on_empty_map
    (check-true (hash-empty? (hash-copy (hash)))))

  (test-case empty_on_nonempty_map
    (check-false (hash-empty? (hash-copy (hash (pair "a" 1))))))

  ;; Regression: value-type values put into a Mutable-Hash<String, Object> require the
  ;; inferred `^v` to widen to Object so the Dictionary gets the right generic
  ;; instantiation and the IL emitter boxes before calling set_Item. Without the fix
  ;; the dictionary storage is laid out for a value type and a boxed access reads
  ;; garbage, which the runtime reports as a "concurrent operations" corruption.
  (test-case put_float_into_object_map
    (let ([m : (Mutable-Hash String Object) (make-hash)])
      (begin
        (hash-set! m "k" 1.5)
        (check-true (hash-has-key? m "k")))))

  (test-case put_int_into_object_map
    (let ([m : (Mutable-Hash String Object) (make-hash)])
      (begin
        (hash-set! m "k" 42)
        (check-true (hash-has-key? m "k")))))

  (test-case put_bool_into_object_map
    (let ([m : (Mutable-Hash String Object) (make-hash)])
      (begin
        (hash-set! m "k" #t)
        (check-true (hash-has-key? m "k"))))))
