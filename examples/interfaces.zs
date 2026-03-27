;; Interface definitions and class implementations
(module interfaces)

(namespace ZScript.Examples)

;; Define an interface with method signatures
(interface IGreeter
  (Greet [] : String))

;; A class implementing the interface
(class HelloGreeter : IGreeter
  [name : String]
  (Greet [] : String name))

;; Interface with multiple methods and parameters
(interface ICalculator
  (Add [a : Int] [b : Int] : Int)
  (Negate [x : Int] : Int))

;; Class implementing ICalculator
(class SimpleCalculator : ICalculator
  (Add [a : Int] [b : Int] : Int (+ a b))
  (Negate [x : Int] : Int (- 0 x)))

;; Interface extending another interface
(interface IAdvancedCalculator : ICalculator
  (Multiply [a : Int] [b : Int] : Int))
