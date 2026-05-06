(namespace ZScheme.Examples)

(module generics)

(import stdlib/list)

;; Generic functions using ^a type variable syntax

;; Identity: returns its argument unchanged
(define (id [x : ^a]) : ^a x)

;; Const: returns the first argument, ignoring the second
(define (const [x : ^a] [y : ^b]) : ^a x)

;; Apply: calls a function on a value
(define (apply [f : (^a -> ^b)] [x : ^a]) : ^b
  (f x))

;; Wrap a value in a list
(define (wrap [x : ^a]) : (List ^a)
  (list x))

;; Compose two functions generically
(define (compose [f : (^b -> ^c)] [g : (^a -> ^b)]) : (^a -> ^c)
  (lambda (x) (f (g x))))

;; Usage: (id 42)              => 42
;; Usage: (id "hello")         => "hello"
;; Usage: (const 1 "ignored")  => 1
;; Usage: (apply inc 5)        => 6
;; Usage: (wrap 99)            => (list 99)
;; Usage: (compose f g)        => (lambda (x) (f (g x)))
