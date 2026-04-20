;; Macros that generate type definitions (records, unions, classes)

(namespace ZScheme.Examples)

(module macros)

;; ---------------------------------------------------------------------------
;; 1. define-dto — macro that generates a record (product type)
;; ---------------------------------------------------------------------------

(define-syntax define-dto
  (syntax-rules ()
    [(define-dto name field ...)
     (record name field ...)]))

(define-dto UserInfo [name : String] [age : Int])
(define-dto Coordinate [x : Int] [y : Int] [z : Int])

(define (make-user [n : String] [a : Int]) : UserInfo
  (UserInfo n a))

(define (origin) : Coordinate
  (Coordinate 0 0 0))

;; ---------------------------------------------------------------------------
;; 2. define-enum — macro that generates a union (sum type)
;; ---------------------------------------------------------------------------

(define-syntax define-enum
  (syntax-rules (case)
    [(define-enum name (case variant field ...) ...)
     (union name (variant field ...) ...)]))

(define-enum Color
  (case Red)
  (case Green)
  (case Blue))

(define-enum Expr
  (case Lit [value : Int])
  (case Add [left : Expr] [right : Expr]))

;; Pattern match on macro-generated union
(define (color-name [c : Color]) : String
  (match c
    [(Red) "red"]
    [(Green) "green"]
    [(Blue) "blue"]))

;; Recursive evaluation of macro-generated expression tree
(define (eval-expr [e : Expr]) : Int
  (match e
    [(Lit v) v]
    [(Add l r) (+ (eval-expr l) (eval-expr r))]))

;; ---------------------------------------------------------------------------
;; 3. define-base-class — macro that generates an open class for inheritance
;; ---------------------------------------------------------------------------

(define-syntax define-base-class
  (syntax-rules ()
    [(define-base-class name member ...)
     (class #:open name member ...)]))

(define-base-class Counter
  [value : Int]
  (define (Current) : Int value)
  (define (Add [n : Int]) : Int (+ value n)))
