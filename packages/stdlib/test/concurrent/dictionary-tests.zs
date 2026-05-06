;; concurrent-dictionary-tests.zs — Tests for Concurrent-Dictionary operations
(namespace ZScheme.StdLib.Tests)
(module concurrent-dictionary-tests)

(import zunit)
(import stdlib/concurrent/dictionary)
(import stdlib/option)

(test-suite ConcurrentDictionaryTests
  (test-case new_dict_is_empty
    (check-true (empty? (concurrent-dictionary/new))))

  (test-case new_dict_has_zero_count
    (check-equal? 0 (length (concurrent-dictionary/new))))

  (test-case put_adds_entry
    (let [d (concurrent-dictionary/new)]
      (begin
        (put! d "a" 1)
        (check-equal? 1 (length d)))))

  (test-case put_updates_entry
    (let [d (concurrent-dictionary/new)]
      (begin
        (put! d "a" 1)
        (put! d "a" 99)
        (check-equal? 1 (length d)))))

  (test-case try_add_returns_true_for_new_key
    (let [d (concurrent-dictionary/new)]
      (check-true (try-add! d "a" 1))))

  (test-case try_add_returns_false_for_existing_key
    (let [d (concurrent-dictionary/new)]
      (begin
        (put! d "a" 1)
        (check-false (try-add! d "a" 2)))))

  (test-case get_returns_some_for_existing
    (let [d (concurrent-dictionary/new)]
      (begin
        (put! d "a" 42)
        (check-true (some? (get d "a"))))))

  (test-case get_returns_none_for_missing
    (let [d : (Concurrent-Dictionary String Int) (concurrent-dictionary/new)]
      (check-true (none? (get d "a")))))

  (test-case try_get_returns_true_for_existing
    (let [d (concurrent-dictionary/new)]
      (begin
        (put! d "a" 42)
        (let [result (try-get d "a")]
          (begin
            (check-true (value/0 result))
            (check-equal? 42 (value/1 result)))))))

  (test-case try_get_returns_false_for_missing
    (let [d : (Concurrent-Dictionary String Int) (concurrent-dictionary/new)]
      (let [result (try-get d "z")]
        (check-false (value/0 result)))))

  (test-case try_remove_existing_key
    (let [d (concurrent-dictionary/new)]
      (begin
        (put! d "a" 42)
        (let [result (try-remove! d "a")]
          (begin
            (check-true (value/0 result))
            (check-equal? 42 (value/1 result))
            (check-equal? 0 (length d)))))))

  (test-case try_remove_missing_key
    (let [d : (Concurrent-Dictionary String Int) (concurrent-dictionary/new)]
      (let [result (try-remove! d "a")]
        (check-false (value/0 result)))))

  (test-case contains_key_true
    (let [d (concurrent-dictionary/new)]
      (begin
        (put! d "a" 1)
        (check-true (contains-key? d "a")))))

  (test-case contains_key_false
    (let [d : (Concurrent-Dictionary String Int) (concurrent-dictionary/new)]
      (check-false (contains-key? d "a"))))

  (test-case clear_removes_all
    (let [d (concurrent-dictionary/new)]
      (begin
        (put! d "a" 1)
        (put! d "b" 2)
        (clear! d)
        (check-true (empty? d))))))
