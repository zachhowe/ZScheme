;; result.zs — Result type for success or failure
(module result)

(union (Result ^a ^e)
  (Ok [value : ^a])
  (Err [error : ^e]))

(define (result/unwrap [res : (Result ^a ^e)]) : ^a
  (match res
    [(Ok v) v]
    [(Err _) (raise "Called unwrap on Err")]))

(define (result/map [res : (Result ^a ^e)] [f : (Fn [^a] ^b)]) : (Result ^b ^e)
  (match res
    [(Ok v) (Ok (f v))]
    [(Err e) (Err e)]))

(define (result/flat-map [res : (Result ^a ^e)] [f : (Fn [^a] (Result ^b ^e))]) : (Result ^b ^e)
  (match res
    [(Ok v) (f v)]
    [(Err e) (Err e)]))

(define (result/ok? [res : (Result ^a ^e)]) : Bool
  (match res
    [(Ok _) #t]
    [(Err _) #f]))

(define (result/err? [res : (Result ^a ^e)]) : Bool
  (match res
    [(Ok _) #f]
    [(Err _) #t]))

(export Result Ok Err result/unwrap result/map result/flat-map result/ok? result/err?)
