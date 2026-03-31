;; array-tests.zs — Tests for Array operations
(namespace ZScheme.StdLib.Tests)
(module array-tests)

(import zunit)
(import stdlib/array)

(test-suite ArrayTests
  (test-case count_returns_length
    (check-equal? 3 (array/count (array 1 2 3))))

  (test-case nth_returns_element
    (check-equal? 20 (array/nth (array 10 20 30) 1)))

  (test-case append_adds_to_end
    (check-equal? 4 (array/count (array/append (array 1 2 3) 4))))

  (test-case set_replaces_element
    (check-equal? 99 (array/nth (array/set (array 1 2 3) 1 99) 1)))

  (test-case empty_on_empty_array
    (check-true (array/empty? (array))))

  (test-case empty_on_nonempty_array
    (check-false (array/empty? (array 1))))

  (test-case map_transforms_elements
    (let [result (array/map (array 1 2 3) (fn [x] (* x 10)))]
      (begin
        (check-equal? 3 (array/count result))
        (check-equal? 10 (array/nth result 0))
        (check-equal? 20 (array/nth result 1))
        (check-equal? 30 (array/nth result 2)))))

  (test-case filter_selects_matching
    (let [result (array/filter (array 1 2 3 4 5) (fn [x] (< x 4)))]
      (begin
        (check-equal? 3 (array/count result))
        (check-equal? 1 (array/nth result 0)))))

  (test-case fold_accumulates
    (check-equal? 6 (array/fold (array 1 2 3) 0 (fn [acc x] (+ acc x))))))
