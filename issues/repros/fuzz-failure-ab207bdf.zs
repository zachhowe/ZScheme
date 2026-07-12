(namespace ZSchemeFuzzed)

(module fuzz_ab207bdf)

(import stdlib/option)
(import stdlib/treelist)
(import stdlib/result)
(import stdlib/vector)
(import stdlib/pipe)
(import stdlib/concurrent/bag)
(import stdlib/mutable/vector)
(import stdlib/mutable/treelist)
(import stdlib/error)
(import stdlib/control)
(import stdlib/catch)

(import-clr
  [fuzz-max-int System.Math/Max : (Int Int -> Int)]
  [fuzz-str-empty? System.String/IsNullOrEmpty : (String -> Bool)])

(define-union (FUn_0 ^a ^b) (Left_0 [lv : ^a]) (Right_0 [rv : ^b]))
(@ System.ObsoleteAttribute "fuzz-deprecated")
(define-union (FUn_1 ^a) :where (^a unmanaged) (Wrap_1 [value : ^a]) (Empty_1))

(define-syntax fuzz-mk-rec-166
  (syntax-rules ()
    [(fuzz-mk-rec-166 name field ...)
     (define-record name field ...)]))

(fuzz-mk-rec-166 MRec_845 [f0 : Int] [f1 : Int] [f2 : Int])

(define-syntax fuzz-let1-373
  (syntax-rules ()
    [(fuzz-let1-373 x v body)
     (let* ([x v]) body)]))

(define-syntax fuzz-min2-7408
  (syntax-rules ()
    [(fuzz-min2-7408 a b)
     (if (< a b) a b)]))

(define-syntax fuzz-hyg-192
  (syntax-rules ()
    [(fuzz-hyg-192 body) (let* ([x0 42]) (+ x0 body))]))

(define-class FCls_0
  [f0 : Int #:mutable]
  [f1 : Int #:mutable]
  (constructor [a0 : Int] [a1 : Int]
    (set! f0 (* a0 a0))
    (set! f1 a0))
  (define (M0_0 [p0 : Int]) : Int (unwrap-or (vector-member (vector (unwrap-or (map (Some (unwrap-or (vector-member (vector (vector-length (vector->mutable-vector (vector 51 4 2147483647))) (if (!= (float->double -404.4992) (float->double -404.4992)) -2147483648 p0)) (vector-foldl (vector-map (vector 97 96 f1 f0) (lambda ([x0 : Int]) f1)) 18968 (lambda ([x1 : Int] [x2 : Int]) x2))) (treelist-length (treelist-add (treelist 5301 f0 p0 85) -2147483648)))) (lambda ([x7 : Int]) (use ([x8 : System.IO.Stream (new System.IO.MemoryStream)]) (treelist-ref (treelist x7 x7 -84834 -61440 p0) 3)))) (treelist-length (treelist-append (treelist) (treelist (let ([x3 (typeof Byte)]) 78) (with-handlers ([System.InvalidOperationException x5] 33881) (with-handlers ([System.DivideByZeroException x4] (if #f 47039 (raise (new System.InvalidOperationException "fuzz")))) (if #f 39 (raise (new System.DivideByZeroException "fuzz"))))) (use ([x6 (new System.IO.MemoryStream)]) 2147483647))))) (let ([x15 : (Option (Result Int String)) (Some (Ok (let ([x9 (match (catch f0) [(Ok x10) x10] [(Err x11) f1])]) (if (= "\r" "\n") 1 0))))])
    (match x15 [(Some (Ok x16)) (let ([x17 (string->symbol "foo")]) (if (= x17 'foo) (match (values 89 0.0) [(values _ x18) p0]) (match (Error/inner (make-error "err654")) [None 0] [(Some _) 1])))] [(Some (Err _)) (let ([x12 (let ([x13 : (Option (Option Int)) (Some (Some p0))])
    (match x13 [(Some (Some x14)) 2147483647] [(Some None) 43] [None f1]))]) (treelist-length (treelist-append (treelist f0 p0 -2147483648 p0 f0) (treelist f0 -16266))))] [None (vector-length (treelist->vector (treelist)))])) (/ (treelist-first (treelist (if #t -27927 f0) (vector-ref (build-vector 1 (lambda ([x19 : Int]) f1)) 0) (let ([x20 (mutable-treelist 15 -2147483648)]) (mutable-treelist-ref x20 1)) (let ([x21 (typeof (Result Int String))]) 19))) 40)) (vector-length (vector->mutable-vector (vector (let ([x22 (vector->mutable-vector (vector (let* ([x23 32] [x24 f0]) 21) (unwrap (Some 14732))))]) (begin (vector-set! x22 0 (let ([x25 : (Result Int String) (Ok 48)])
    (match (map x25 (lambda ([x26 : Int]) f1)) [(Ok x27) -2147483648] [(Err _) p0]))) (vector-ref x22 0))))))) (string->int (int->string (treelist-length (treelist-filter (treelist (match 'foo ['zz f1] ['foo f1] ['x1 92] [_ 10]) (- -55643 f0) (if (= (string->symbol "x1") 'x1) 30 -78525) (let ([x28 : (Result Int String) (Ok p0)])
    (match (map x28 (lambda ([x29 : Int]) 27)) [(Ok x30) -65588] [(Err _) 26]))) (lambda ([x31 : Int]) (fuzz-str-empty? "qaqvr")))))))))

(define (f0 [x32 : ^a]) : ^a
  x32)

(@ System.Diagnostics.DebuggerStepThroughAttribute)
(define (f1 [x33 : (Int -> Int)] [x34 : Int]) : Int
  (let ([x35 (some? (Some (use ([x36 (new System.IO.StringWriter)]) (if (= (string-append "\"" "ahi") (string-append "" "eer")) 1 0))))]) (unwrap-or (Some (let ([x37 (vector->mutable-vector (vector (let ([x38 (vector->mutable-vector (vector (let ([x39 (mutable-treelist x34 x34 x34)]) (begin (mutable-treelist-add! x39 -2147483648) (mutable-treelist-add! x39 x34) (mutable-treelist-add! x39 x34) (mutable-treelist-length x39))) (treelist-fold (treelist x34 -2147483648 93 33861) -2147483648 (lambda ([x40 : Int] [x41 : Int]) x40)) (treelist-first (treelist 40 80022 51 x34 x34)) (match (catch x34) [(Ok x42) x42] [(Err x43) x34])))]) (begin (vector-set! x38 0 (if (= (symbol->string 'my-sym) "a") x34 61)) (vector-ref x38 0))) (match (values (let ([x44 : (Option (Result (Option Int) String)) (Some (Ok (Some 59139)))])
    (match x44 [(Some (Ok (Some x45))) 61] [(Some (Ok None)) x34] [(Some (Err _)) x34] [None 95088])) (begin (unless #t () ()) 17)) [(values x46 3) (if (= (symbol->string 'foo) "x1") -62774 -2147483648)] [_ (treelist-length (treelist-add (treelist -2147483648 x34) 2147483647))]) (float->int (int->float 778808))))]) (begin (vector-set! x37 2 (x33 (let ([x47 : (Result Int String) (Ok -20352)])
    (match x47 [(Ok x48) x34] [(Err _) 10])))) (vector-ref x37 2)))) (vector-ref (vector (fuzz-max-int (treelist-length (treelist-rest (treelist 59 x34))) (match (values x34 -1.0) [(values _ x49) 20])) (with-handlers ([System.InvalidOperationException x50] (if (= "\"" "") 1 0)) ([System.DivideByZeroException x51] (MRec_845/f1 (with (MRec_845 -57238 2147483647 x34) [f1 34] [f2 x34] [f0 -47583]))) (/ (treelist-ref (treelist x34 88) 0) (- x34 x34)))) 1))))

(@ System.Diagnostics.DebuggerStepThroughAttribute)
(define (f2 [x52 : Int] [x53 : Int]) : Int
  (if (<= x52 0) (treelist-length (treelist-map (treelist) (lambda ([x54 : Int]) (unwrap-or (flat-map (Some (let ([x55 (vector->mutable-vector (vector x52))]) (begin (unless #f (vector-set! x55 0 -69614)) (vector-ref x55 0)))) (lambda ([x58 : Int]) (Some (treelist-length (treelist))))) (let ([x56 : (Result Int String) (Ok 52997)])
    (match x56 [(Ok x57) 30] [(Err _) -19307])))))) (f2 (- x52 1) (if (> 1.0 (double->float (float->double -728.9797))) (vector-length (treelist->vector (treelist))) (if #t (vector-length (vector 33 x53)) ((partial f1 (lambda ([x59 : Int]) (use* ([x60 (new System.Threading.CancellationTokenSource)] [x61 (new System.IO.StringWriter)]) x53))) 99))))))

(define (compute) : Int
  (if (= (symbol->string 'x1) "x1") 14 (let ([x62 (concurrent-bag/new)]) (begin (add! x62 (vector-length (vector-append (vector (with-handlers ([System.InvalidOperationException x64] (match (values -2147483648 -11244 76) [(values -1 x65 x66) x65] [_ 30])) (with-handlers ([System.DivideByZeroException x63] (if #f 88 48760)) (if (vector-empty? (vector 39257 87)) (vector-ref (make-vector 1 -2147483648) 0) (raise (new System.DivideByZeroException "fuzz"))))) (treelist-length (treelist-rest (treelist (vector-length (treelist->vector (treelist 79 -2147483648 -2147483648 77 -2147483648))) (let ([x67 : (Result (Option Int) String) (Ok (Some -2147483648))])
    (match x67 [(Ok (Some x68)) x68] [(Ok None) -82826] [(Err _) 7])) (f0 2147483647) (treelist-first (treelist 2147483647 75795 -22192)) (let ([x69 (vector->mutable-vector (vector -64529))]) (begin (when #f (vector-set! x69 0 -2147483648)) (vector-ref x69 0)))))) (vector-length (vector->mutable-vector (vector (MRec_845/f2 (MRec_845 18 83032 55)) (match 'zz ['a 29] ['zz 16230] ['b 2147483647] [_ 11]))))) (vector 7)))) (value/1 (try-take! x62))))))
