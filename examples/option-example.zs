;; Option type — values that may or may not exist
;; Option<T> has two cases: (Some value) and None

(namespace ZScript.Examples)

(module options)

(import option)

(define (find-positive [x : Int]) : (Option Int)
  (if (> x 0) (Some x) None))

(define (day-of-week [n : Int]) : (Option String)
  (match n
    [1 (Some "Monday")] [2 (Some "Tuesday")]
    [3 (Some "Wednesday")] [4 (Some "Thursday")]
    [5 (Some "Friday")] [6 (Some "Saturday")]
    [7 (Some "Sunday")] [_ None]))

(define (describe-option [opt : (Option Int)]) : String
  (match opt
    [(Some v) (string-append "Got: " (int->string v))]
    [None "Nothing here"]))

(define (or-else [opt : (Option Int)] [default : Int]) : Int
  (match opt
    [(Some v) v]
    [None default]))
