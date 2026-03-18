;; String operations and conversions

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
