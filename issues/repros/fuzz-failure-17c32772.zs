(namespace ZSchemeFuzzed)

(module fuzz_17c32772)

(import aux_17c32772_0)

(import stdlib/option)
(import stdlib/core)
(import stdlib/cond)
(import stdlib/concurrent/bag)

(import-clr
  [fuzz-min-int System.Math/Min : (Int Int -> Int)]
  [fuzz-max-int System.Math/Max : (Int Int -> Int)]
  [fuzz-abs-flt System.Math/Abs : (Double -> Double)]
  [fuzz-int-to-long System.Convert/ToInt64 : (Int -> Long)]
  [fuzz-long-to-int System.Convert/ToInt32 : (Long -> Int)]
  [fuzz-abs-long System.Math/Abs : (Long -> Long)]
  [fuzz-min-long System.Math/Min : (Long Long -> Long)]
  [fuzz-max-long System.Math/Max : (Long Long -> Long)]
  [fuzz-big-mul System.Math/BigMul : (Int Int -> Long)])

(define-union (FUn_0 ^a) (Wrap_0 [value : ^a]) (Empty_0))
(define-union (FUn_1 ^a) (Wrap_1 [value : ^a]) (Empty_1))

(define-record (FRec_0 ^a ^b) [first : ^a] [second : ^b])

(define-type-alias (FuzzAlias ^k ^v) System.Collections.Generic.Dictionary)
(define (fuzzaliasfn [m : (FuzzAlias Int Int)]) : Int
  0)

(define-syntax fuzz-when-6178
  (syntax-rules ()
    [(fuzz-when-6178 cond body)
     (if cond body 0)]))

(define-interface IFuz_0
  (M0_0  : Float))

(define (vf0 [x25 : Int ...]) : Int
  0)

(define (compute) : Int
  (use ([x26 : System.IO.Stream (new System.IO.MemoryStream)]) (aux_17c32772_0/h2 (unwrap-or (map (Some (fuzz-long-to-int (fuzz-int-to-long (if (= (string->symbol (symbol->string 'my-sym)) 'a) 22 78)))) (lambda ([x27 : Int]) (unwrap-or (map (Some (match (Some 2) [(Some x28) x27] [None x27])) (lambda ([x29 : Int]) (fuzz-max-int x29 8))) (if #f 76 71)))) -36823) (FRec_0/first (FRec_0 (match (values (* 11 -2147483648 2147483647) (fuzz-long-to-int (fuzz-big-mul 39822 5)) (FRec_0/second (FRec_0 62 -12327)) (unwrap-or (flat-map (Some 81206) (lambda ([x30 : Int]) (Some x30))) -2147483648)) [(values _ x31 x32 x33) (unwrap-or (flat-map (Some 39833) (lambda ([x34 : Int]) (Some 4))) x32)]) (id (aux_17c32772_0/h1 48)))))))
