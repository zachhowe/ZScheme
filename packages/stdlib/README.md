# ZScheme Standard Library

Core data types, collections, and utilities for ZScheme programs.

## Installation

Add to your `package.zspkg` dependencies:

```scheme
(dependencies
  (zscheme
    [stdlib :local "../stdlib"]))
```

## Import

Import individual modules with the `stdlib/` prefix:

```scheme
(import stdlib/option)
(import stdlib/treelist)
(import stdlib/pipe)
```

## Modules

| Module | Description |
|--------|-------------|
| `stdlib/core` | `id`, `compose`, `is-null?` |
| `stdlib/option` | `Option` union (`Some`, `None`) with `unwrap`, `unwrap-or`, `map`, `flat-map`, `some?`, `none?` |
| `stdlib/result` | `Result` union (`Ok`, `Err`) with `unwrap`, `map`, `flat-map`, `ok?`, `err?` |
| `stdlib/error` | `ErrorInfo` record and `Error` constructor |
| `stdlib/list` | Pure singly linked list — `List` union (`Cons`, `Nil`), `list`, `cons`, `list-head`, `list-tail`, `rest`, `empty?`, `length`, `list-ref`, `reverse`, `map`, `filter`, `fold`, `append`, `concat`, plus conversions (`treelist->list`, `list->treelist`, `array->list`, `list->array`, etc.) |
| `stdlib/treelist` | AVL-tree-backed immutable list — `length`, `list-ref`, `list-head`, `list-tail`, `cons`, `append`, `concat`, `empty?`, `map`, `filter`, `fold` |
| `stdlib/array` | Immutable array — `array-length`, `array-ref`, `append`, `set`, `array-empty?`, `map`, `filter`, `fold` |
| `stdlib/map` | Immutable dictionary — `map-of`, `pair`, `get`, `put`, `remove`, `contains-key?`, `empty?`, `keys`, `values` |
| `stdlib/string` | `format`, `equals?`, `empty?`, `starts-with?`, `ends-with?` |
| `stdlib/math` | `sqrt`, `abs`, `min`, `max`, `floor`, `ceiling`, `minf`, `maxf` |
| `stdlib/datetime` | `utc-now`, `datetime-subtract`, `timespan-total-seconds` |
| `stdlib/task` | `task-completed-task` |
| `stdlib/catch` | `catch` macro — convert exceptions to `(Result T ErrorInfo)` |
| `stdlib/cond` | `cond` macro — multi-branch conditional |
| `stdlib/pipe` | `\|>` macro — pipe operator |
| `stdlib/attrs` | `with-method-impl` — attribute helper for `aggressive-inlining`, `no-inlining`, `no-optimization` |
| `stdlib/mutable/treelist` | Mutable tree-list — `length`, `list-ref`, `list-set!`, `add!`, `insert!`, `remove-at!`, `clear!`, `contains?`, `empty?` |
| `stdlib/mutable/array` | Mutable array — `array-length`, `array-ref`, `array-set!`, `array-empty?` |
| `stdlib/mutable/map` | Mutable dictionary — `new`, `count`, `put!`, `get`, `remove!`, `contains-key?`, `clear!`, `empty?`, `keys`, `values` |
| `stdlib/concurrent/bag` | Thread-safe bag — `new`, `count`, `empty?`, `add!`, `try-take!`, `try-peek` |
| `stdlib/concurrent/queue` | Thread-safe queue — `new`, `count`, `empty?`, `enqueue!`, `try-dequeue!`, `try-peek` |
| `stdlib/concurrent/stack` | Thread-safe stack — `new`, `count`, `empty?`, `push!`, `clear!`, `try-pop!`, `try-peek` |
| `stdlib/concurrent/dictionary` | Thread-safe dictionary — `new`, `count`, `empty?`, `put!`, `try-add!`, `get`, `try-get`, `try-remove!`, `contains-key?`, `clear!`, `keys`, `values` |

## Usage

### Option and Result

```scheme
(import stdlib/option)
(import stdlib/result)
(import stdlib/error)

;; Option
(option/unwrap (Some 42))                    ;; => 42
(option/unwrap-or (None) 0)                  ;; => 0
(option/map (Some 5) (lambda (x) (* x 2)))      ;; => (Some 10)

;; Result
(define (safe-div [a : Int] [b : Int]) : (Result Int ErrorInfo)
  (if (= b 0)
    (Err (Error "division by zero"))
    (Ok (/ a b))))

(result/map (Ok 10) (lambda (x) (* x 2)))       ;; => (Ok 20)
```

### Collections

```scheme
(import stdlib/treelist)
(import stdlib/map)

(define nums (treelist 1 2 3 4 5))
(map nums (lambda (x) (* x 2)))               ;; => (2 4 6 8 10)
(filter nums (lambda (x) (> x 3)))            ;; => (4 5)
(fold nums 0 (lambda (acc x) (+ acc x)))      ;; => 15

(define scores (map-of (pair "alice" 95) (pair "bob" 87)))
(get scores "alice")                          ;; => (Some 95)
(put scores "carol" 91)                       ;; => new map with carol added
```

### Singly Linked List

```scheme
(import stdlib/list)

;; Create with the variadic constructor
(define nums (list 1 2 3 4 5))

;; Construct with cons
(cons 0 nums)                                 ;; => (0 1 2 3 4 5)

;; Access
(list-head nums)                              ;; => 1
(list-tail nums)                              ;; => (2 3 4 5)
(length nums)                                 ;; => 5

;; Transform
(map nums (lambda (x) (* x 2)))               ;; => (2 4 6 8 10)
(filter nums (lambda (x) (> x 3)))            ;; => (4 5)
(fold nums 0 (lambda (acc x) (+ acc x)))      ;; => 15

;; Pattern match on the union
(define (sum [xs : (List Int)]) : Int
  (match xs
    [Nil 0]
    [(Cons h t) (+ h (sum t))]))

;; Convert to/from other collection types
(list->treelist (list 1 2 3))                 ;; => (treelist 1 2 3)
(treelist->list (treelist 1 2 3))             ;; => (list 1 2 3)
```

### Pipe and Catch

```scheme
(import stdlib/pipe)
(import stdlib/catch)
(import stdlib/result)
(import stdlib/error)

;; Pipe threads a value through a series of functions
(|> (list 1 2 3 4 5)
    (filter (lambda (x) (> x 2)))
    (map (lambda (x) (* x 10))))

;; Catch converts exceptions to Result values
(catch (/ 10 0))  ;; => (Err (ErrorInfo "..." None))
```

## Dependencies

- **NuGet** — `System.Collections.Immutable 9.0.0`
