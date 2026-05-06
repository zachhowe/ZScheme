;; Interface definitions and class implementations
(module interfaces)

(namespace ZScheme.Examples)

;; Define an interface with method signatures
(define-interface IGreeter
  (Greet [] : String))

;; A class implementing the interface
(define-class HelloGreeter : IGreeter
  [name : String]
  (define (Greet) : String name))

;; Interface with multiple methods and parameters
(define-interface ICalculator
  (Add [a : Int] [b : Int] : Int)
  (Negate [x : Int] : Int))

;; Class implementing ICalculator
(define-class SimpleCalculator : ICalculator
  (define (Add [a : Int] [b : Int]) : Int (+ a b))
  (define (Negate [x : Int]) : Int (- 0 x)))

;; Interface extending another interface
(define-interface IAdvancedCalculator : ICalculator
  (Multiply [a : Int] [b : Int] : Int))
