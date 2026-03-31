(module inheritance)

;; Base class must be marked :open to allow subclassing
(class : open Animal
  [name : String]
  [sound : String]
  (Speak [] : String
    (string-append (string-append name " says ") sound)))

;; Dog inherits name and sound from Animal, adds breed
;; Constructor: (Dog "Rex" "Woof" "Labrador")
(class : open Dog : Animal
  [breed : String]
  (Speak [] : String
    (string-append (string-append name " the ") breed)))

;; GuideDog further extends Dog
(class GuideDog : Dog
  [handler : String]
  (Speak [] : String
    (string-append (super/Speak) handler)))

;; Explicit constructor with computed base class args
(class : open NamedAnimal
  [name : String]
  [sound : String]
  (constructor [display-name : String]
    (set! name display-name)
    (set! sound "..."))
  (Speak [] : String
    (string-append name sound)))
