;; Anonymous object expressions implementing interfaces

(namespace ZScheme.Examples)

(module objects)

;; Define interfaces
(interface IGreeter
  (Greet [name : String] : String))

(interface IFarewell
  (Goodbye [name : String] : String))

;; Simple object implementing a single interface
(define greeting
  (object IGreeter
    (Greet [name : String] : String
      (string-append "Hello, " name))))

;; Object implementing multiple interfaces
(define polite
  (object (IGreeter IFarewell)
    (Greet [name : String] : String
      (string-append "Welcome, " name))
    (Goodbye [name : String] : String
      (string-append "Farewell, " name))))

;; Object that captures a variable from the enclosing scope
(define (make-greeter [prefix : String]) : IGreeter
  (object IGreeter
    (Greet [name : String] : String
      (string-append prefix name))))
