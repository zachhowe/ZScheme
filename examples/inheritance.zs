(module inheritance)

(import stdlib/string)

;; Base class must be marked #:open to allow subclassing
(define-class #:open Animal
  [name : String]
  [sound : String]
  (define (Speak) : String
    (format "{0} says {1}" name sound)))

;; Dog inherits name and sound from Animal, adds breed
;; Constructor: (Dog "Rex" "Woof" "Labrador")
(define-class #:open Dog : Animal
  [breed : String]
  (define (Speak) : String
    (format "{0} the {1}" name breed)))

;; GuideDog further extends Dog
(define-class GuideDog : Dog
  [handler : String]
  (define (Speak) : String
    (format "{0}{1}" (super/Speak) handler)))

;; Explicit constructor with computed base class args
(define-class #:open NamedAnimal
  [name : String]
  [sound : String]
  (constructor [display-name : String]
    (set! name display-name)
    (set! sound "..."))
  (define (Speak) : String
    (format "{0}{1}" name sound)))
