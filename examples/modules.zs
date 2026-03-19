;; Module and import syntax demonstration
;;
;; module declares the current file's module name.
;; import brings another module's definitions into scope.
;; Note: these parse and type-check but do not resolve
;; across files in the current compiler.

(namespace ZScript.Examples)

(module math/utils)

;; Definitions in this module
(define (abs [x : Int]) : Int
  (if (< x 0) (- 0 x) x))

(define (max [a : Int] [b : Int]) : Int
  (if (> a b) a b))

(define (min [a : Int] [b : Int]) : Int
  (if (< a b) a b))
