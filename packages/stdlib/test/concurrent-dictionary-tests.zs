;; concurrent-dictionary-tests.zs — Tests for Concurrent-Dictionary operations
(namespace ZScheme.StdLib.Tests)
(module concurrent-dictionary-tests)

(import zunit)
(import stdlib/concurrent-dictionary)
(import stdlib/option)

(test-suite ConcurrentDictionaryTests
  (test-case new_dict_is_empty
    (check-true (concurrent-dictionary/empty? (concurrent-dictionary/new))))

  (test-case new_dict_has_zero_count
    (check-equal? 0 (concurrent-dictionary/count (concurrent-dictionary/new))))

  (test-case put_adds_entry
    (let [d (concurrent-dictionary/new)]
      (begin
        (concurrent-dictionary/put! d "a" 1)
        (check-equal? 1 (concurrent-dictionary/count d)))))

  (test-case put_updates_entry
    (let [d (concurrent-dictionary/new)]
      (begin
        (concurrent-dictionary/put! d "a" 1)
        (concurrent-dictionary/put! d "a" 99)
        (check-equal? 1 (concurrent-dictionary/count d)))))

  (test-case try_add_returns_true_for_new_key
    (let [d (concurrent-dictionary/new)]
      (check-true (concurrent-dictionary/try-add! d "a" 1))))

  (test-case try_add_returns_false_for_existing_key
    (let [d (concurrent-dictionary/new)]
      (begin
        (concurrent-dictionary/put! d "a" 1)
        (check-false (concurrent-dictionary/try-add! d "a" 2)))))

  (test-case get_returns_some_for_existing
    (let [d (concurrent-dictionary/new)]
      (begin
        (concurrent-dictionary/put! d "a" 42)
        (check-true (option/some? (concurrent-dictionary/get d "a"))))))

  (test-case get_returns_none_for_missing
    (let [d : (Concurrent-Dictionary String Int) (concurrent-dictionary/new)]
      (check-true (option/none? (concurrent-dictionary/get d "a")))))

  (test-case try_get_returns_true_for_existing
    (let [d (concurrent-dictionary/new)]
      (begin
        (concurrent-dictionary/put! d "a" 42)
        (let [result (concurrent-dictionary/try-get d "a")]
          (begin
            (check-true (tuple/first result))
            (check-equal? 42 (tuple/second result)))))))

  (test-case try_get_returns_false_for_missing
    (let [d : (Concurrent-Dictionary String Int) (concurrent-dictionary/new)]
      (let [result (concurrent-dictionary/try-get d "z")]
        (check-false (tuple/first result)))))

  (test-case try_remove_existing_key
    (let [d (concurrent-dictionary/new)]
      (begin
        (concurrent-dictionary/put! d "a" 42)
        (let [result (concurrent-dictionary/try-remove! d "a")]
          (begin
            (check-true (tuple/first result))
            (check-equal? 42 (tuple/second result))
            (check-equal? 0 (concurrent-dictionary/count d)))))))

  (test-case try_remove_missing_key
    (let [d : (Concurrent-Dictionary String Int) (concurrent-dictionary/new)]
      (let [result (concurrent-dictionary/try-remove! d "a")]
        (check-false (tuple/first result)))))

  (test-case contains_key_true
    (let [d (concurrent-dictionary/new)]
      (begin
        (concurrent-dictionary/put! d "a" 1)
        (check-true (concurrent-dictionary/contains-key? d "a")))))

  (test-case contains_key_false
    (let [d : (Concurrent-Dictionary String Int) (concurrent-dictionary/new)]
      (check-false (concurrent-dictionary/contains-key? d "a"))))

  (test-case clear_removes_all
    (let [d (concurrent-dictionary/new)]
      (begin
        (concurrent-dictionary/put! d "a" 1)
        (concurrent-dictionary/put! d "b" 2)
        (concurrent-dictionary/clear! d)
        (check-true (concurrent-dictionary/empty? d))))))
