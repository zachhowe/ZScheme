;; result.zs — Result type for success or failure
(module result)

(import stdlib/option)

(define-union (Result ^a ^e)
  (Ok [value : ^a])
  (Err [error : ^e]))

(define (unwrap [res : (Result ^a ^e)]) : ^a
  (match res
    [(Ok v) v]
    [(Err _) (raise (new System.Exception "Called unwrap on Err"))]))

(define (unwrap-or [res : (Result ^a ^e)] [default : ^a]) : ^a
  (match res
    [(Ok v) v]
    [(Err _) default]))

(define (unwrap-or-none [res : (Result ^a ^e)]) : (Option ^a)
  (match res
    [(Ok v) (Some v)]
    [(Err _) None]))

(define (map [res : (Result ^a ^e)] [f : (^a -> ^b)]) : (Result ^b ^e)
  (match res
    [(Ok v) (Ok (f v))]
    [(Err e) (Err e)]))

(define (flat-map [res : (Result ^a ^e)] [f : (^a -> (Result ^b ^e))]) : (Result ^b ^e)
  (match res
    [(Ok v) (f v)]
    [(Err e) (Err e)]))

(define (ok? [res : (Result ^a ^e)]) : Bool
  (match res
    [(Ok _) #t]
    [(Err _) #f]))

(define (err? [res : (Result ^a ^e)]) : Bool
  (match res
    [(Ok _) #f]
    [(Err _) #t]))

(export Result Ok Err unwrap unwrap-or unwrap-or-none map flat-map ok? err?)
