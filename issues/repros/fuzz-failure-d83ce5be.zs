(namespace ZSchemeFuzzed)

(module fuzz_d83ce5be)

(import stdlib/option)
(import stdlib/pipe)
(import stdlib/concurrent/dictionary)

(import-clr
  [fuzz-sqrt System.Math/Sqrt : (Double -> Double)]
  [fuzz-str-len System.String.Length :instance-property : (String -> Int)]
  [fuzz-try-parse System.Int32/TryParse : (String -> (ValueTuple Bool Int))]
  [fuzz-min-dbl System.Math/Min : (Double Double -> Double)]
  [fuzz-max-dbl System.Math/Max : (Double Double -> Double)]
  [fuzz-floor-dbl System.Math/Floor : (Double -> Double)])

(define-union (FUn_0 ^a) (Both_0 [a : ^a] [b : ^a]) (Neither_0))
(define-union (FUn_1 ^a) (Cons_1 [head : ^a] [tail : (FUn_1 ^a)]) (Nil_1))

(define-record (FRec_0 ^a) [x : ^a] [y : ^a])
(define-record (FRec_1 ^a) [x : ^a] [y : ^a])

(define-syntax fuzz-mk-rec-916
  (syntax-rules ()
    [(fuzz-mk-rec-916 name field ...)
     (define-record name field ...)]))

(fuzz-mk-rec-916 MRec_688 [f0 : Int] [f1 : Int] [f2 : Int])

(define-syntax fuzz-hyg-388
  (syntax-rules ()
    [(fuzz-hyg-388 body) (let* ([x0 42]) (+ x0 body))]))

(define-interface IFuz_0
  (M0_0  : Float))
(define-interface IFuz_1
  (M1_0 [p0 : Int] [p1 : Int] : Int)
  (M1_1 [p0 : Int] : Int)
  (M1_2 [p0 : Float] [p1 : Int] : Int))

(define (f0 [x0 : ^a] [x1 : ^b]) : ^a
  x0)

(define (compute) : Int
  (with-handlers ([System.Exception x2] (fuzz-hyg-388 (* (let ([x3 : IFuz_1 (object IFuz_1
  (define (M1_0 [p0 : Int] [p1 : Int]) : Int x0)
  (define (M1_1 [p0 : Int]) : Int (with-handlers ([System.InvalidOperationException x5] p0) (with-handlers ([System.DivideByZeroException x4] p0) (if #t p0 (raise (new System.InvalidOperationException "fuzz"))))))
  (define (M1_2 [p0 : Float] [p1 : Int]) : Int (unwrap-or (map (Some 41) (lambda ([x6 : Int]) 46)) -58236)))]) x0) (fuzz-str-len (string-append (string-append (string-append (string-append "\r" "uzl") "\t") "mwdybf") ""))))) (use ([x7 (new System.IO.MemoryStream)]) (if (let* ([x8 (float->int (int->float -842626))] [x9 (* (let* ([x10 ((partial f0 5) x8)] [x11 (let* ([x12 (lambda ([x13 : Int]) (match (Some 64) [(Some x14) 2147483647] [None 100]))] [x15 (lambda ([x16 : Int]) (unwrap-or (flat-map (Some x16) (lambda ([x17 : Int]) (Some x8))) x16))] [x18 (lambda ([x19 : Int]) (let ([x20 : IFuz_0 (object IFuz_0
  (define (M0_0) : Float -0.0))]) 2147483647))] [x21 (lambda ([x22 : Int]) (+ 36 x10 x10 x22 -54241))]) (|> x8 x12 x15 x18 x21))]) (+ 21 x11 x8 19 x8)) (/ -2147483648 -1))]) (value/0 (fuzz-try-parse "5931"))) -2147483648 (raise (new System.InvalidOperationException "fuzz"))))))
