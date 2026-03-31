;; String operations and conversions

(namespace ZScript.Examples)

(module strings)

;; Concatenate first and last name
(define (full-name [first : String] [last : String]) : String
  (string-append (string-append first " ") last))

;; Create a label from a number
(define (label [prefix : String] [n : Int]) : String
  (string-append prefix (int->string n)))

;; Greeting with a count
(define (greeting-with-count [name : String] [count : Int]) : String
  (string-append
    (string-append "Hello, " name)
    (string-append "! Visit #" (int->string count))))

;; Variadic function example
(import-clr [clr-join System.String/Join : (Fn [String (Mutable-Array String)] String)])

(define (join-all [sep : String] [parts : String ...]) : String
  (clr-join sep parts))
