;; vector-tests.zs — Tests for Vector operations
(namespace ZScript.StdLib.Tests)
(module vector-tests)

(import zunit)
(import vector)

(test-suite VectorTests
  (test-case count_returns_length
    (check-equal? 3 (vector/count (vector 1 2 3))))

  (test-case nth_returns_element
    (check-equal? 20 (vector/nth (vector 10 20 30) 1)))

  (test-case append_adds_to_end
    (check-equal? 4 (vector/count (vector/append (vector 1 2 3) 4))))

  (test-case set_replaces_element
    (check-equal? 99 (vector/nth (vector/set (vector 1 2 3) 1 99) 1)))

  (test-case empty_on_empty_vector
    (check-true (vector/empty? (vector))))

  (test-case empty_on_nonempty_vector
    (check-false (vector/empty? (vector 1))))

  (test-case map_transforms_elements
    (let [result (vector/map (vector 1 2 3) (fn [x] (* x 10)))]
      (begin
        (check-equal? 3 (vector/count result))
        (check-equal? 10 (vector/nth result 0))
        (check-equal? 20 (vector/nth result 1))
        (check-equal? 30 (vector/nth result 2)))))

  (test-case filter_selects_matching
    (let [result (vector/filter (vector 1 2 3 4 5) (fn [x] (< x 4)))]
      (begin
        (check-equal? 3 (vector/count result))
        (check-equal? 1 (vector/nth result 0)))))

  (test-case fold_accumulates
    (check-equal? 6 (vector/fold (vector 1 2 3) 0 (fn [acc x] (+ acc x))))))
