;; Object expressions with class inheritance

(namespace ZScript.Examples)

(module object-inheritance)

;; Base class must be :open to allow subclassing
(class : open Animal
  [name : String]
  [sound : String]
  (Speak [] : String
    (string-append (string-append name " says ") sound)))

;; Anonymous object inheriting from a base class (parameterless base ctor not needed
;; when using an explicit constructor with super args)
(define cat
  (object : Animal
    (constructor (super "Cat" "meow"))
    (Speak [] : String "A cat says meow")))

;; Object inheriting from base class and overriding a method with super/ call
(define loud-dog
  (object : Animal
    (constructor (super "Dog" "woof"))
    (Speak [] : String
      (string-append (super/Speak) "!!!"))))

;; Interface for additional behavior
(interface IDescribable
  (Describe [] : String))

;; Object inheriting from base class AND implementing an interface
(define parrot
  (object : Animal IDescribable
    (constructor (super "Parrot" "squawk"))
    (Speak [] : String
      (string-append (super/Speak) " (repeated)"))
    (Describe [] : String
      "A colorful parrot")))
