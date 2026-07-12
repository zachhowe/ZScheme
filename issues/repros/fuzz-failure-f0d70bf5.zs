(namespace ZSchemeFuzzed)

(module fuzz_f0d70bf5)

(import aux_f0d70bf5_0)
(import aux_f0d70bf5_1)

(import stdlib/option)
(import stdlib/vector)
(import stdlib/hash)
(import stdlib/cond)
(import stdlib/pipe)
(import stdlib/list)
(import stdlib/concurrent/bag)
(import stdlib/error)

(import-clr
  [fuzz-int-to-long System.Convert/ToInt64 : (Int -> Long)]
  [fuzz-long-to-int System.Convert/ToInt32 : (Long -> Int)]
  [fuzz-abs-long System.Math/Abs : (Long -> Long)]
  [fuzz-min-long System.Math/Min : (Long Long -> Long)]
  [fuzz-max-long System.Math/Max : (Long Long -> Long)]
  [fuzz-big-mul System.Math/BigMul : (Int Int -> Long)])

(define-union (FUn_0 ^a ^b) (Left_0 [lv : ^a]) (Right_0 [rv : ^b]))
(define-union (FUn_1 ^a) (Wrap_1 [value : ^a]) (Empty_1))

(define-record (FRec_0 ^a) [x : ^a] [y : ^a])

(define-struct SRec_0 [f0 : Int] [f1 : Int] [f2 : Int])
(define-struct SRec_1 [x : Int] [y : Int])

(define-syntax fuzz-when-4576
  (syntax-rules ()
    [(fuzz-when-4576 cond body)
     (if cond body 0)]))

(define-syntax fuzz-let1-9552
  (syntax-rules ()
    [(fuzz-let1-9552 x v body)
     (let* ([x v]) body)]))

(define-syntax fuzz-min2-3365
  (syntax-rules ()
    [(fuzz-min2-3365 a b)
     (if (< a b) a b)]))

(define-syntax fuzz-lit-7138
  (syntax-rules (plus minus)
    [(fuzz-lit-7138 plus a b) (+ a b)]
    [(fuzz-lit-7138 minus a b) (- a b)]))

(define (compute) : Int
  (let* ([x53 (vector-argmin (vector (vector-ref (build-vector 1 (lambda ([x54 : Int]) (fuzz-long-to-int (fuzz-big-mul (with-handlers ([System.InvalidOperationException x56] 94) (with-handlers ([System.DivideByZeroException x55] (if #f 10 (raise (new System.InvalidOperationException "fuzz")))) (if #f 41 (raise (new System.DivideByZeroException "fuzz"))))) (hash-count (hash-remove (hash (pair "tus" -46289) (pair "fu" x54) (pair "hnx" x54) (pair "f" x54)) "f")))))) 0) (vector-length (vector 38 (use* ([x57 (new System.Threading.CancellationTokenSource)] [x58 (new System.IO.StringWriter)]) (hash-count (hash-set (hash (pair "qis" 72) (pair "ia" 8) (pair "jwc" -2147483648)) "y" -25845))))) (length (vector->list (vector 12653 (SRec_0/f0 (SRec_0 (/ 2906 38 60) (FRec_0/x (FRec_0 -2147483648 48)) (if (= (symbol->string 'b) "x1") 29 2147483647))) (let ([x59 (string->symbol (symbol->string 'b))]) (if (= x59 (string->symbol (symbol->string 'b))) (let* ([x60 21830] [x61 x60]) x60) (unwrap-or (map (Some 5) (lambda ([x62 : Int]) 28)) 2147483647)))))) (if (>= (let ([x63 -0.0]) 22154) 19 20 (unwrap-or (map (Some 2147483647) (lambda ([x64 : Int]) -41178)) 18)) (begin (= 57 -65200) (length (filter Nil (lambda ([x65 : Int]) #f))) (length (reverse Nil))) (match (values (with-handlers ([System.InvalidOperationException x67] 49) (with-handlers ([System.DivideByZeroException x66] (if #t -28017 (raise (new System.InvalidOperationException "fuzz")))) (if #f -16878 (raise (new System.DivideByZeroException "fuzz"))))) (vector-length (vector-append (vector 14) (vector 51 -2147483648 33234 58 12))) (let ([x68 : (Option (Option Int)) (Some (Some 6))])
    (match x68 [(Some (Some x69)) 36] [(Some None) 28] [None 36141])) (% 27 28) (length (vector->list (vector -19932 -20544))) (fuzz-long-to-int (fuzz-big-mul 82 22117))) [(values -2 _ x70 0 x71 _) (vector-length (vector x70 -35304 x70))] [_ (* 2147483647 35 -16003 6 -82320)]))) (lambda ([x72 : Int]) (if (not (none? (Some x72))) (float->int (int->float 65330)) (match (values (if (= (fuzz-int-to-long x72) (fuzz-int-to-long x72)) 1 0) -377.9778) [(values _ x73) (fuzz-long-to-int (fuzz-big-mul 93 -2147483648))]))))] [x74 (fuzz-when-4576 (not #t) (list-ref (Cons (vector-ref (vector-sort (vector x53 x53) (lambda ([x75 : Int] [x76 : Int]) (> x75 x76))) 1) Nil) 0))]) (match (Left_0 (let ([x77 (with-handlers ([System.Exception x78] (match (values 89 -788.2333) [(values x79 x80) x74])) (use ([x81 (new System.IO.MemoryStream)]) (if (hash-has-key? (hash (pair "p" x74) (pair "ua" 73)) "u") (vector-ref (vector-set/copy (vector x74 2) 0 18043) 0) (raise (new System.InvalidOperationException "fuzz")))))]) (unwrap-or (hash-ref (hash (pair "dcd" (unwrap-or (Some x74) -2147483648)) (pair "mpo" (let ([x82 #f]) x53)) (pair "i" (float->int (int->float -544298))) (pair "qt" (aux_f0d70bf5_0/h0 -6753 89))) "qt") (cond [#t 76459] [#t x53] [else x53])))) [(Left_0 _) (let ([x83 (typeof Double?)]) 42)] [(Right_0 x84) (let* ([x87 (lambda ([x88 : Int]) (match (Some (hash-count (hash-remove (hash (pair "lna" x53) (pair "ht" 12) (pair "jdw" 34)) "ht"))) [(Some x94) (cond [#t 2147483647] [#t x84] [#f x74] [else 18])] [None (match (values x84 77 x88 87971 82 x84 x84) [(values x89 x90 x91 _ x92 x93 _) x90])]))] [x95 (lambda ([x96 : Int]) (if (!= (string->symbol (symbol->string 'zz)) (string->symbol "b")) (vector-length (list->vector Nil)) (vector-length (vector-append (vector 18 x74 x53 x53 x53) (vector -2147483648)))))] [x97 (lambda ([x98 : Int]) (begin (>= x53 2147483647) (SRec_0/f1 (with (SRec_0 x84 -63055 25) [f1 x74] [f0 2147483647])) (length (cdr (Cons x74 Nil))) (vector-length (vector-append (vector x53) (vector x74)))))] [x99 (lambda ([x100 : Int]) (vector-length (vector-filter (vector (fold (map Nil (lambda ([x101 : Int]) 33326)) x74 (lambda ([x102 : Int] [x103 : Int]) x103)) (vector-foldl (vector-map (vector x53) (lambda ([x104 : Int]) 6)) 8956 (lambda ([x105 : Int] [x106 : Int]) 2)) (unwrap-or (hash-ref (hash (pair "auo" 54655) (pair "cq" x53) (pair "dv" 86)) "p") 18299) (vector-length (vector-append (vector x100 76675 -39392 42 66) (vector 28)))) (lambda ([x107 : Int]) (not #t)))))]) (|> (SRec_1/x (SRec_1 (fold Nil x74 (lambda ([x85 : Int] [x86 : Int]) -98497)) (if (!= (float->double 700.8674) (float->double -787.983)) 32973 45))) x87 x95 x97 x99))])))
