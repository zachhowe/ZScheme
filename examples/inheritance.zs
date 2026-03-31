(module inheritance)

(import stdlib/string)

;; Base class must be marked :open to allow subclassing
(class : open Animal
  [name : String]
  [sound : String]
  (Speak [] : String
    (string/format "{0} says {1}" name sound)))

;; Dog inherits name and sound from Animal, adds breed
;; Constructor: (Dog "Rex" "Woof" "Labrador")
(class : open Dog : Animal
  [breed : String]
  (Speak [] : String
    (string/format "{0} the {1}" name breed)))

;; GuideDog further extends Dog
(class GuideDog : Dog
  [handler : String]
  (Speak [] : String
    (string/format "{0}{1}" (super/Speak) handler)))

;; Explicit constructor with computed base class args
(class : open NamedAnimal
  [name : String]
  [sound : String]
  (constructor [display-name : String]
    (set! name display-name)
    (set! sound "..."))
  (Speak [] : String
    (string/format "{0}{1}" name sound)))
