;; slist-tests.zs — Tests for SList (singly linked list)
(namespace ZScheme.StdLib.Tests)
(module slist-tests)

(import zunit)
(import stdlib/slist)

(test-suite SListTests
  (test-case empty_is_empty
    (check-true (slist/empty? SNil)))

  (test-case cons_is_not_empty
    (check-false (slist/empty? (SCons 1 SNil))))

  (test-case empty_returns_snil
    (check-true (slist/empty? (slist/empty))))

  (test-case head_returns_first
    (check-equal? 1 (slist/head (SCons 1 (SCons 2 SNil)))))

  (test-case tail_returns_rest
    (check-equal? 2 (slist/head (slist/tail (SCons 1 (SCons 2 SNil))))))

  (test-case rest_returns_rest
    (check-equal? 2 (slist/head (slist/rest (SCons 1 (SCons 2 SNil))))))

  (test-case rest_of_empty_is_empty
    (check-true (slist/empty? (slist/rest SNil))))

  (test-case cons_prepends
    (check-equal? 0 (slist/head (slist/cons 0 (SCons 1 SNil)))))

  (test-case length_empty
    (check-equal? 0 (slist/length SNil)))

  (test-case length_nonempty
    (check-equal? 3 (slist/length (SCons 1 (SCons 2 (SCons 3 SNil))))))

  (test-case nth_returns_element
    (check-equal? 20 (slist/nth (SCons 10 (SCons 20 (SCons 30 SNil))) 1)))

  (test-case nth_returns_first
    (check-equal? 10 (slist/nth (SCons 10 (SCons 20 SNil)) 0)))

  (test-case reverse_empty
    (check-true (slist/empty? (slist/reverse SNil))))

  (test-case reverse_nonempty
    (check-equal? 3 (slist/head (slist/reverse (SCons 1 (SCons 2 (SCons 3 SNil)))))))

  (test-case reverse_preserves_length
    (check-equal? 3 (slist/length (slist/reverse (SCons 1 (SCons 2 (SCons 3 SNil)))))))

  (test-case map_transforms_elements
    (let [result (slist/map (SCons 1 (SCons 2 (SCons 3 SNil))) (fn [x] (* x 2)))]
      (begin
        (check-equal? 3 (slist/length result))
        (check-equal? 2 (slist/nth result 0))
        (check-equal? 4 (slist/nth result 1))
        (check-equal? 6 (slist/nth result 2)))))

  (test-case map_empty
    (check-true (slist/empty? (slist/map SNil (fn [x] (* x 2))))))

  (test-case filter_selects_matching
    (let [result (slist/filter (SCons 1 (SCons 2 (SCons 3 (SCons 4 (SCons 5 SNil))))) (fn [x] (> x 3)))]
      (begin
        (check-equal? 2 (slist/length result))
        (check-equal? 4 (slist/nth result 0))
        (check-equal? 5 (slist/nth result 1)))))

  (test-case filter_empty
    (check-true (slist/empty? (slist/filter SNil (fn [x] (> x 0))))))

  (test-case fold_accumulates
    (check-equal? 15 (slist/fold (SCons 1 (SCons 2 (SCons 3 (SCons 4 (SCons 5 SNil))))) 0 (fn [acc x] (+ acc x)))))

  (test-case fold_empty
    (check-equal? 0 (slist/fold SNil 0 (fn [acc x] (+ acc x)))))

  (test-case append_adds_to_end
    (let [result (slist/append (SCons 1 (SCons 2 (SCons 3 SNil))) 4)]
      (begin
        (check-equal? 4 (slist/length result))
        (check-equal? 1 (slist/head result))
        (check-equal? 4 (slist/nth result 3)))))

  (test-case concat_joins_lists
    (let [result (slist/concat (SCons 1 (SCons 2 SNil)) (SCons 3 (SCons 4 (SCons 5 SNil))))]
      (begin
        (check-equal? 5 (slist/length result))
        (check-equal? 1 (slist/head result))
        (check-equal? 5 (slist/nth result 4)))))

  (test-case concat_empty_left
    (check-equal? 1 (slist/head (slist/concat SNil (SCons 1 (SCons 2 SNil))))))

  (test-case concat_empty_right
    (check-equal? 2 (slist/length (slist/concat (SCons 1 (SCons 2 SNil)) SNil)))))
