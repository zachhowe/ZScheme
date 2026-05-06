;; option.zs — Option type for values that may or may not exist
(module option)

(define-union (Option ^a)
  (Some [value : ^a])
  (None))

(define (unwrap [opt : (Option ^a)]) : ^a
  (match opt
    [(Some v) v]
    [None (raise (new System.Exception "Called unwrap on None"))]))

(define (unwrap-or [opt : (Option ^a)] [default : ^a]) : ^a
  (match opt
    [(Some v) v]
    [None default]))

(define (map [opt : (Option ^a)] [f : (^a -> ^b)]) : (Option ^b)
  (match opt
    [(Some v) (Some (f v))]
    [None None]))

(define (flat-map [opt : (Option ^a)] [f : (^a -> (Option ^b))]) : (Option ^b)
  (match opt
    [(Some v) (f v)]
    [None None]))

(define (some? [opt : (Option ^a)]) : Bool
  (match opt
    [(Some _) #t]
    [None #f]))

(define (none? [opt : (Option ^a)]) : Bool
  (match opt
    [(Some _) #f]
    [None #t]))

(export Option Some None unwrap unwrap-or map flat-map some? none?)
