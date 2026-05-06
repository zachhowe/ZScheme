;; list-tests.zs — Tests for List operations
(namespace ZScheme.StdLib.Tests)
(module list-tests)

(import zunit)
(import stdlib/list)

(test-suite ListTests
  (test-case count_returns_length
    (check-equal? 3 (list/count (list 1 2 3))))

  (test-case nth_returns_element
    (check-equal? 20 (list/nth (list 10 20 30) 1)))

  (test-case head_returns_first
    (check-equal? 1 (list/head (list 1 2 3))))

  (test-case tail_removes_first
    (check-equal? 2 (list/count (list/tail (list 1 2 3)))))

  (test-case cons_prepends
    (check-equal? 0 (list/head (list/cons 0 (list 1 2 3)))))

  (test-case append_adds_to_end
    (check-equal? 4 (list/count (list/append (list 1 2 3) 4))))

  (test-case concat_joins_lists
    (check-equal? 5 (list/count (list/concat (list 1 2) (list 3 4 5)))))

  (test-case empty_on_empty_list
    (check-true (list/empty? (list))))

  (test-case empty_on_nonempty_list
    (check-false (list/empty? (list 1))))

  (test-case map_transforms_elements
    (let [result (list/map (list 1 2 3) (lambda (x) (* x 2)))]
      (begin
        (check-equal? 3 (list/count result))
        (check-equal? 2 (list/nth result 0))
        (check-equal? 4 (list/nth result 1))
        (check-equal? 6 (list/nth result 2)))))

  (test-case filter_selects_matching
    (let [result (list/filter (list 1 2 3 4 5) (lambda (x) (> x 3)))]
      (begin
        (check-equal? 2 (list/count result))
        (check-equal? 4 (list/nth result 0))
        (check-equal? 5 (list/nth result 1)))))

  (test-case fold_accumulates
    (check-equal? 15 (list/fold (list 1 2 3 4 5) 0 (lambda (acc x) (+ acc x))))))
