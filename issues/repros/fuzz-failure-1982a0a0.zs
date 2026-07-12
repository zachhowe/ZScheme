(namespace ZSchemeFuzzed)

(module fuzz_1982a0a0)

(import stdlib/option)
(import stdlib/treelist)
(import stdlib/result)
(import stdlib/vector)
(import stdlib/math)
(import stdlib/core)
(import stdlib/list)
(import stdlib/concurrent/dictionary)
(import stdlib/mutable/treelist)

(import-clr
  [fuzz-max-int System.Math/Max : (Int Int -> Int)]
  [fuzz-str-empty? System.String/IsNullOrEmpty : (String -> Bool)]
  [fuzz-str-len System.String.Length :instance-property : (String -> Int)])

(define-union (FUn_0 ^a) (Wrap_0 [value : ^a]) (Empty_0))

(@ System.SerializableAttribute)
(define-record (FRec_0 ^a ^b) [first : ^a] [second : ^b])
(define-struct (FRec_1 ^a) [x : ^a] [y : ^a])

(define-struct SRec_0 [f0 : Int] [f1 : Int] [f2 : Int])

(define-interface IFuz_0
  (M0_0 [p0 : Int] : Float)
  (M0_1 [p0 : Float] : Int)
  (M0_2  : Int))
(@ System.ObsoleteAttribute "fuzz-deprecated")
(define-interface IFuz_1
  (M1_0 [p0 : Float] [p1 : Int] : Int)
  (M1_1  : Int))

(define-class #:open FCls_0
  [f0 : Int #:mutable]
  [f1 : Int #:mutable]
  [f2 : Int #:mutable]
  (define (M0_0 [p0 : Int]) : Int (treelist-length (treelist-add (treelist (length (map Nil (lambda ([x0 : Int]) (vector-length (vector-drop (vector (if #f 66 47) (+ 16 x0)) 0)))))) (let ([x10 : (Option (Result Int String)) (Some (Ok (let ([x3 : (Result Int String) (Ok (with-handlers ([System.InvalidOperationException x2] 69) (with-handlers ([System.DivideByZeroException x1] (if #f f0 (raise (new System.InvalidOperationException "fuzz")))) (if #f f2 (raise (new System.DivideByZeroException "fuzz"))))))])
    (match x3 [(Ok x4) (match (values f1 -1.0) [(values x5 x6) 88])] [(Err _) (float->int (int->float -716719))]))))])
    (match x10 [(Some (Ok x11)) (unwrap-or (vector-member (vector (% f0 79) (vector-length (vector-map (vector f0 p0 81471 2147483647 10) (lambda ([x12 : Int]) f0)))) (let ([x13 (concurrent-dictionary/new)]) (begin (put! x13 0 -2147483648) (value/1 (try-remove! x13 0))))) (let ([x14 : (Result Int String) (Ok 99)])
    (match x14 [(Ok x15) -66814] [(Err _) p0])))] [(Some (Err _)) (vector-length (vector-filter (vector (/ f0 15) (treelist-length (treelist-cons 16 (treelist 18 97262 25))) (string->int (int->string f2)) (let ([x7 (concurrent-dictionary/new)]) (begin (put! x7 0 89) (value/1 (try-remove! x7 0))))) (lambda ([x8 : Int]) (if #f #f #f))))] [None (vector-length (vector-append (vector ((lambda ([x9 : Int]) f1) f2) (treelist-length (treelist-append (treelist 54 5 f2 58 80581) (treelist -20372 f1 12)))) (vector (list-ref (Cons f0 (Cons f2 (Cons f0 Nil))) 1))))]))))))

(define-class FCls_1 : FCls_0
  (define (M0_0 [p0 : Int]) : Int (super/M0_0 p0)))

(define (f0 [x16 : Int] [x17 : Int]) : Int
  (- (with-handlers ([System.Exception x18] (if (= (symbol->string 'x1) "x1") ((lambda ([x17 : Int]) (% 0 x16)) (vector-ref (vector x17) 0)) (treelist-length (treelist-append (treelist (let ([x19 (mutable-treelist 80 -21603)]) (mutable-treelist-ref x19 0)) (vector-length (vector x16 x16 x17 x16))) (treelist (treelist-length (list->treelist Nil))))))) (use ([x20 (new System.IO.StringWriter)]) (if (none? (Some (unwrap (Some (vector-foldl (vector-map (vector x17 x17) (lambda ([x21 : Int]) 4)) 5130 (lambda ([x22 : Int] [x23 : Int]) -36106)))))) (id (let ([x25 : (Result Int String) (Ok ((lambda ([x24 : Int]) -2147483648) -2147483648))])
    (match x25 [(Ok x26) (string->int (int->string x16))] [(Err _) (id -95803)]))) (raise (new System.InvalidOperationException "fuzz")))))))

(define (f1 [x27 : ^a] [x28 : ^b]) : ^a :where ((^a unmanaged) (^b unmanaged))
  x27)

(define (fuzz-run-func [f : (delegate System.Func<int,int>)]) : Int
  (f 10))

(define (fuzz-run-action [a : (delegate System.Action)]) : Unit
  (a))

(define (fuzz-deleg-fn [x : Int]) : Int
  (* x 2))

(define (compute) : Int
  (match (values (unwrap-or (vector-member (vector ((partial f1 (with-handlers ([System.InvalidOperationException x30] (fold Nil 12 (lambda ([x33 : Int] [x34 : Int]) 27))) (with-handlers ([System.DivideByZeroException x29] (let* ([x31 23] [x32 77]) 99)) (if (treelist-empty? (treelist 66 43 84 54561)) (match (Some 30) [(Some x35) -27075] [None -97532]) (raise (new System.InvalidOperationException "fuzz")))))) (vector-argmax (vector (length (vector->list (vector 15 -12540 -70062))) (vector-count (vector 2 93088 62) (lambda ([x36 : Int]) #f))) (lambda ([x37 : Int]) (match (Some 55) [(Some x38) x38] [None 80])))) (- (vector-ref (vector-set/copy (vector (unwrap-or (flat-map (Some -2147483648) (lambda ([x39 : Int]) (Some 98547))) -2147483648) (match (Some 79) [(Some x40) 29] [None -8468]) (let* ([x41 2147483647] [x42 -2147483648] [x43 x41]) -80282)) 0 (treelist-length (list->treelist Nil))) 0) (vector-ref (vector (length (rest Nil)) (float->int (int->float -450204)) (treelist-length (treelist)) (let ([x44 (mutable-treelist 2147483647 46 65803)]) (mutable-treelist-ref x44 0)) (vector-count (vector -2147483648 65 -60389 -2147483648) (lambda ([x45 : Int]) #t))) 1)) ((partial f1 (list-ref (Cons 32 (Cons 86404 (Cons -2147483648 Nil))) 1)) (with-handlers ([System.InvalidOperationException x47] (treelist-first (treelist 57767))) (with-handlers ([System.DivideByZeroException x46] (if (< 89 3) (let ([x48 : (Option (Option Int)) (Some (Some 8))])
    (match x48 [(Some (Some x49)) x49] [(Some None) -2147483648] [None -2147483648])) (raise (new System.InvalidOperationException "fuzz")))) (if (!= 74 19970) (- 39937) (raise (new System.DivideByZeroException "fuzz")))))) (if (= (float->double (double->float (sqrt (float->double 724.1615)))) (float->double (double->float (sqrt (float->double 724.1615))))) (treelist-length (treelist-map (treelist (fuzz-run-func (lambda ([x50 : Int]) 28234)) (vector-length (vector-filter (vector 11530 2147483647 28016 67 2147483647) (lambda ([x51 : Int]) #f))) (begin 0.0 -1.0 46001 2147483647) (f1 59656 36) (treelist-first (treelist 36844 -50725 14))) (lambda ([x52 : Int]) (match (Wrap_0 30482) [(Wrap_0 2) 76] [Empty_0 x52] [_ x52])))) (if (= (float->double (maxf 73.40647 -357.6379)) (float->double (maxf 73.40647 -357.6379))) (treelist-length (treelist-add (treelist -15270 45902 -2147483648 44927) 86)) (let ([x53 (concurrent-dictionary/new)]) (begin (put! x53 0 33935) (length x53)))))) (unwrap (Some (let ([x57 : (Option (Result (Option Int) String)) (Some (Ok (Some (treelist-length (treelist-add (treelist -13368 -2147483648 -32029 80 97) 71)))))])
    (match x57 [(Some (Ok (Some x58))) (if (!= (float->double -1.0) (float->double -1.0)) -88294 x58)] [(Some (Ok None)) ((compose (lambda ([x54 : Int]) -2147483648) (lambda ([x55 : Int]) 85)) 2147483647)] [(Some (Err _)) (match (Wrap_0 43) [(Wrap_0 x56) 2147483647] [Empty_0 32])] [None (vector-ref (vector-set/copy (vector -83071) 0 73797) 0)]))))) -93014) (fold (Cons (length (map Nil (lambda ([x59 : Int]) (match (Wrap_0 x59) [(Wrap_0 x60) 2147483647] [Empty_0 x59])))) (Cons (let ([x61 (string->symbol (symbol->string 'x1))]) (if (= x61 'x1) 66 -56797)) (Cons 43549 Nil))) ((lambda ([x62 : Int]) (if (= (float->double (minf 1.0 -0.0)) (float->double -1.0)) (if (!= (float->double -1.0) (float->double -445.3866)) 2147483647 x62) (vector-ref (vector-sort (vector x62 x62 66) (lambda ([x63 : Int] [x64 : Int]) (> x63 x64))) 0))) (string->int (int->string (begin 2147483647 -2147483648)))) (lambda ([x65 : Int] [x66 : Int]) (f0 (f0 (f1 52736 x65) (match (values 47 x65) [(values 2 x67) 56] [_ 2147483647])) (treelist-length (treelist-filter (treelist (unwrap (Some x66)) (fuzz-run-func (lambda ([x68 : Int]) 29)) (length (vector->list (vector x65 4738 -2147483648 x66))) (let ([x69 : (Result Int String) (Ok x66)])
    (match (map x69 (lambda ([x70 : Int]) -87512)) [(Ok x71) x66] [(Err _) x65]))) (lambda ([x72 : Int]) (and #f #f))))))) (match (string->symbol "b") ['b ((lambda ([x73 : Int]) (with-handlers ([System.Exception x76] (let ([x77 'b]) (if (= x77 'b) x73 53))) (use ([x78 (new System.IO.StringWriter)]) (if (= 91 70) (% x73 -30) (raise (new System.InvalidOperationException "fuzz")))))) (match Nil [Nil (let ([x74 (mutable-treelist 31 -45957)]) (mutable-treelist-ref x74 1))] [(Cons x75 Nil) (if (f1 #f 29) 1 0)] [_ (begin (fuzz-run-action (lambda () ())) 64)]))] ['my-sym (* (fuzz-max-int ((compose (lambda ([x79 : Int]) x79) (lambda ([x80 : Int]) 56)) 5) (match Nil [Nil 96] [(Cons x81 _) -2147483648])) (vector-length (vector (/ 18404 53) (float->int (int->float -904832)) (vector-length (vector-map (vector 85 -2147483648 56 10) (lambda ([x82 : Int]) x82))) (- -87341) (begin 2147483647 100 -68686))) (match (values (fuzz-max-int -2147483648 63) (let ([x83 #t]) -2147483648) (% -2147483648 97) (length (reverse Nil)) (vector-length (vector-filter-not (vector 92568 20 66) (lambda ([x84 : Int]) #t))) (if (= "srgqq" "pkjd") 1 0) (if (= (symbol->string 'b) "my-sym") 35 -2147483648)) [(values x85 _ _ _ x86 x87 _) (let ([x88 (typeof IFuz_1)]) 9)]))] ['foo (unwrap (Some (treelist-length (treelist-add (treelist (treelist-length (treelist-map (treelist -2533 -2147483648 2147483647 18) (lambda ([x89 : Int]) x89))) (% 4 77) (vector-length (vector-append (vector -2147483648 -2147483648 -2147483648 91) (vector 49))) (% 43 66) (length (vector->list (vector -25363 93 15 83 27171)))) 51))))] [_ (% -2147483648 -1)])) [(values -1 x91 _) (length (concat (Cons (if (!= (float->double (* 35.28107 1.0 1.0 -347.584 299.8436)) (float->double -0.0)) (match (Wrap_0 x91) [(Wrap_0 x92) -2147483648] [Empty_0 x91]) (use* ([x93 (new System.IO.StringWriter)] [x94 (new System.Threading.CancellationTokenSource)] [x95 (new System.Threading.CancellationTokenSource)]) x91)) Nil) (Cons ((partial f0 (match Nil [Nil x91] [(Cons x96 _) x91])) (fuzz-max-int x91 2147483647)) (Cons (vector-ref (vector-set/copy (vector 2147483647) 0 4) 0) Nil))))] [_ (treelist-first (treelist (id (vector-length (vector-filter-not (vector (vector-length (vector-map (vector 2147483647 -52698 2147483647) (lambda ([x97 : Int]) 0))) (vector-length (vector-filter-not (vector -44312 13) (lambda ([x98 : Int]) #f))) (f0 2147483647 2147483647) (id 38) (vector-foldl (vector-map (vector 57294) (lambda ([x99 : Int]) 87998)) -2309 (lambda ([x100 : Int] [x101 : Int]) x101))) (lambda ([x102 : Int]) (and #f #f))))) (length (Cons (SRec_0/f0 (SRec_0 44 92 75)) (Cons 38989 Nil)))))]))
