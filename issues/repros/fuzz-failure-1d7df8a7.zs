(namespace ZSchemeFuzzed)

(module fuzz_1d7df8a7)

(import stdlib/option)
(import stdlib/treelist)
(import stdlib/result)
(import stdlib/concurrent/queue)
(import stdlib/concurrent/stack)
(import stdlib/concurrent/dictionary)
(import stdlib/mutable/treelist)

(import-clr
  [fuzz-min-dbl System.Math/Min : (Double Double -> Double)]
  [fuzz-max-dbl System.Math/Max : (Double Double -> Double)]
  [fuzz-floor-dbl System.Math/Floor : (Double -> Double)])

(define-union (FUn_0 ^a) (Both_0 [a : ^a] [b : ^a]) (Neither_0))
(define-union (FUn_1 ^a ^b) (Left_1 [lv : ^a]) (Right_1 [rv : ^b]))

(define-record (FRec_0 ^a) [x : ^a] [y : ^a])

(@ System.ObsoleteAttribute "fuzz-deprecated")
(define-interface IFuz_0
  (M0_0 [p0 : Int] : Int))

(define (f0 [x0 : (Int -> Int)] [x1 : Int]) : Int
  (if (if (let* ([x2 (use* ([x3 (new System.IO.MemoryStream)] [x4 (new System.IO.StringWriter)]) ((lambda ([x5 : Int]) x1) 47063))] [x6 (+ x2 (let ([x8 : (Result Int String) (Ok (let ([x7 (concurrent-dictionary/new)]) (begin (put! x7 0 15) (value/1 (try-remove! x7 0)))))])
    (match x8 [(Ok x9) (let ([x10 (typeof (TreeList Int))]) 23)] [(Err _) (FRec_0/x (with (FRec_0 64 7) [x 25]))])))] [x11 (* (let ([x12 (concurrent-dictionary/new)]) (begin (put! x12 0 x6) (length x12))) (let ([x13 'foo]) (if (= x13 'foo) -1018 55)))]) (match (- x2 92 -17899) [3 (none? (Some x2))] [-2 (> -316.5262 498.7475)] [_ (if #f #t #t)])) #t (match (match (Some (treelist-fold (treelist 77869 x1 x1 17) 50 (lambda ([x14 : Int] [x15 : Int]) 2147483647))) [(Some x16) (let ([x17 (concurrent-dictionary/new)]) (begin (put! x17 0 86) (value/1 (try-remove! x17 0))))] [None (if (!= 'zz 'zz) x1 86)]) [0 (and (= 816.8589 -474.3736 855.8666 839.4182) (not #f))] [x18 (and #f #t)])) (match (values (x0 (let ([x21 : (Option (Option Int)) (Some (Some (+ 2147483647 x1)))])
    (match x21 [(Some (Some x22)) (unwrap (Some x22))] [(Some None) ((lambda ([x19 : Int]) x19) 31)] [None (let ([x20 (concurrent-stack/new)]) (begin (push! x20 -27352) (value/1 (try-pop! x20))))]))) (+ (double->float (float->double 448.2779)) (double->float (float->double (double->float (fuzz-floor-dbl (float->double 437.6503))))))) [(values x23 x24) (let ([x25 (concurrent-dictionary/new)]) (begin (put! x25 0 (string->int (int->string (use* ([x26 (new System.IO.MemoryStream)] [x27 (new System.Threading.CancellationTokenSource)]) x1)))) (value/1 (try-remove! x25 0))))]) (match (Left_1 (if (some? (Some (treelist-length (treelist-map (treelist x1) (lambda ([x28 : Int]) 89208))))) (treelist-length (treelist-map (treelist (if (= "heiol" "zz") 1 0)) (lambda ([x29 : Int]) (let ([x30 "\t"]) x1)))) -2147483648)) [(Left_1 x31) (unwrap-or (Some (if (= (float->double (- 865.2836 -1.0)) (float->double (- 865.2836 -1.0))) (match (values x31 -267.2593) [(values x32 _) 88]) (if #f x31 x1))) (with-handlers ([System.InvalidOperationException x34] (let ([x35 : (Result Int String) (Ok 93)])
    (match x35 [(Ok x36) 72886] [(Err _) 2147483647]))) (with-handlers ([System.DivideByZeroException x33] (if (let ([x38 : (Result Int String) (Ok x31)]) (err? x38)) (let* ([x39 x31] [x40 x1]) 31495) (raise (new System.InvalidOperationException "fuzz")))) (if (let ([x37 : (Result Int String) (Ok 78)]) (ok? x37)) (treelist-length (treelist-add (treelist 14 -2147483648) 3)) (raise (new System.DivideByZeroException "fuzz"))))))] [(Right_1 x41) (treelist-first (treelist (FRec_0/x (FRec_0 (FRec_0/x (FRec_0 x41 x1)) (let ([x42 : (Option (Result Int String)) (Some (Ok x41))])
    (match x42 [(Some (Ok x43)) 90] [(Some (Err _)) 25] [None x41])))) (if (= 'x1 (string->symbol (symbol->string 'b))) (let ([x44 (concurrent-stack/new)]) (begin (push! x44 15) (value/1 (try-pop! x44)))) (match (values x1 x41 x1) [(values -2 1 x45) 30] [_ x41])) (let ([x51 : (Option (Result (Option Int) String)) (Some (Ok (Some (let ([x46 : (Option (Option Int)) (Some (Some x41))])
    (match x46 [(Some (Some x47)) -39967] [(Some None) 42] [None 28])))))])
    (match x51 [(Some (Ok (Some x52))) (with-handlers ([System.Exception x53] 82) (if #t 63 (raise (new System.Exception "fuzz"))))] [(Some (Ok None)) (let* ([x48 43] [x49 (+ x48 x48)]) x48)] [(Some (Err _)) (with-handlers ([System.DivideByZeroException x50] -566) (/ -2147483648 (- x1 x1)))] [None (string->int (int->string 73))])) (treelist-length (treelist-cons (treelist-length (treelist x41 x41)) (treelist (x0 79) (unwrap-or (Some 16) 35) (let ([x54 'my-sym]) (if (= x54 'my-sym) x41 58)) (x0 x1)))) (let ([x57 : (Result (Option Int) String) (Ok (Some (treelist-fold (treelist x1 x41 63) 22 (lambda ([x55 : Int] [x56 : Int]) x41))))])
    (match x57 [(Ok (Some x58)) (FRec_0/x (FRec_0 x1 -2147483648))] [(Ok None) (FRec_0/x (FRec_0 78 -23629))] [(Err _) 28]))))])))

(define (compute) : Int
  (if (= (symbol->string 'my-sym) "my-sym") (treelist-length (treelist-cons (string->int (int->string (if (= (symbol->string 'my-sym) "b") (match (string->symbol "a") ['a 2147483647] [_ 31120]) (let ([x80 (concurrent-queue/new)]) (begin (enqueue! x80 35) (value/1 (try-dequeue! x80))))))) (treelist (unwrap-or (map (Some (let ([x61 : (Result Int String) (Ok (let ([x59 'a]) (if (= x59 (string->symbol (symbol->string 'a))) -2147483648 19921)))])
    (match (flat-map x61 (lambda ([x62 : Int]) (Ok (let ([x63 (concurrent-queue/new)]) (begin (enqueue! x63 50178) (enqueue! x63 x62) (length x63)))))) [(Ok x64) (if (= 'foo (string->symbol "foo")) 19 x64)] [(Err _) (let ([x60 (mutable-treelist -2147483648 -2147483648)]) (begin (mutable-treelist-add! x60 60) (mutable-treelist-length x60)))]))) (lambda ([x65 : Int]) (let ([x67 : (Result Int String) (Ok (let ([x66 #t]) x65))]) (unwrap x67)))) (if (= (string-append "" (string-append "\"" (string-append "\r" "ahtvi"))) "\n") 1 0)) (if (treelist-empty? (treelist (FRec_0/x (FRec_0 25 2147483647)) (let ([x68 : (Result Int String) (Ok -2147483648)])
    (match (map x68 (lambda ([x69 : Int]) x69)) [(Ok x70) -2147483648] [(Err _) 44])))) (match (values (FRec_0 (treelist-length (treelist-map (treelist 2147483647 89 3) (lambda ([x71 : Int]) 40))) (treelist-length (treelist 55 2147483647))) (let ([x72 : (Option (Result Int String)) (Some (Ok -26599))])
    (match x72 [(Some (Ok x73)) 46057] [(Some (Err _)) 2147483647] [None 49]))) [(values (FRec_0 x74 x75) _) (with-handlers ([System.DivideByZeroException x76] -47502) (/ 2 (- x75 x75)))] [_ (treelist-length (treelist-append (treelist 24699 2147483647 37 10) (treelist 35594 11)))]) (let ([x77 (concurrent-dictionary/new)]) (begin (put! x77 0 (unwrap-or (Some -74986) 74)) (put! x77 1 ((lambda ([x78 : Int]) 88859) 22)) (put! x77 2 (let ([x79 (mutable-treelist 80 92)]) (begin (mutable-treelist-add! x79 62368) (mutable-treelist-add! x79 33650) (mutable-treelist-add! x79 95) (mutable-treelist-length x79)))) (length x77))))))) (let ([x81 (concurrent-dictionary/new)]) (begin (put! x81 0 ((partial f0 (lambda ([x82 : Int]) (let ([x83 : (Option (Result Int String)) (Some (Ok -22535))])
    (match x83 [(Some (Ok x84)) 69] [(Some (Err _)) 76] [None x82])))) ((partial f0 (lambda ([x85 : Int]) 36902)) (* 61 2147483647)))) (length x81)))))
