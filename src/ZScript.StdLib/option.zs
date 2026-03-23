;; option.zs — Option type for values that may or may not exist
(module option)

(union (Option ^a)
  (Some [value : ^a])
  (None))

(define (option/unwrap [opt : (Option ^a)]) : ^a
  (match opt
    [(Some v) v]
    [None (raise "Called unwrap on None")]))

(define (option/unwrap-or [opt : (Option ^a)] [default : ^a]) : ^a
  (match opt
    [(Some v) v]
    [None default]))

(define (option/map [opt : (Option ^a)] [f : (Fn [^a] ^b)]) : (Option ^b)
  (match opt
    [(Some v) (Some (f v))]
    [None None]))

(define (option/flat-map [opt : (Option ^a)] [f : (Fn [^a] (Option ^b))]) : (Option ^b)
  (match opt
    [(Some v) (f v)]
    [None None]))

(define (option/some? [opt : (Option ^a)]) : Bool
  (match opt
    [(Some _) #t]
    [None #f]))

(define (option/none? [opt : (Option ^a)]) : Bool
  (match opt
    [(Some _) #f]
    [None #t]))

(export Option Some None option/unwrap option/unwrap-or option/map
        option/flat-map option/some? option/none?)
