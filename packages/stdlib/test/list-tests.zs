;; list-tests.zs — Tests for List operations
(namespace ZScheme.StdLib.Tests)
(module list-tests)

(import zunit)
(import stdlib/list)

(test-suite ListTests
  (test-case count_returns_length
    (check-equal? 3 (length (list 1 2 3))))

  (test-case nth_returns_element
    (check-equal? 20 (list-ref (list 10 20 30) 1)))

  (test-case head_returns_first
    (check-equal? 1 (list-head (list 1 2 3))))

  (test-case tail_removes_first
    (check-equal? 2 (length (list-tail (list 1 2 3)))))

  (test-case cons_prepends
    (check-equal? 0 (list-head (cons 0 (list 1 2 3)))))

  (test-case append_adds_to_end
    (check-equal? 4 (length (append (list 1 2 3) 4))))

  (test-case concat_joins_lists
    (check-equal? 5 (length (concat (list 1 2) (list 3 4 5)))))

  (test-case empty_on_empty_list
    (check-true (empty? (list))))

  (test-case empty_on_nonempty_list
    (check-false (empty? (list 1))))

  (test-case map_transforms_elements
    (let [result (map (list 1 2 3) (lambda (x) (* x 2)))]
      (begin
        (check-equal? 3 (length result))
        (check-equal? 2 (list-ref result 0))
        (check-equal? 4 (list-ref result 1))
        (check-equal? 6 (list-ref result 2)))))

  (test-case filter_selects_matching
    (let [result (filter (list 1 2 3 4 5) (lambda (x) (> x 3)))]
      (begin
        (check-equal? 2 (length result))
        (check-equal? 4 (list-ref result 0))
        (check-equal? 5 (list-ref result 1)))))

  (test-case fold_accumulates
    (check-equal? 15 (fold (list 1 2 3 4 5) 0 (lambda (acc x) (+ acc x)))))

  ;; Scheme aliases on List: cons / car / cdr should dispatch to list/* via
  ;; type-based overload resolution when both stdlib/list and stdlib/slist
  ;; are in scope.

  (test-case car_returns_first_on_list
    (check-equal? 1 (car (cons 1 (cons 2 (list))))))

  (test-case cdr_returns_rest_on_list
    (check-equal? 2 (car (cdr (cons 1 (cons 2 (list)))))))

  (test-case cons_builds_list
    (check-equal? 3 (length (cons 1 (cons 2 (cons 3 (list))))))))
