(namespace ZSchemeFuzzed)

(module fuzz_d805abd3)

(import aux_d805abd3_0)

(import stdlib/option)
(import stdlib/treelist)
(import stdlib/result)
(import stdlib/string)
(import stdlib/math)
(import stdlib/list)
(import stdlib/concurrent/stack)
(import stdlib/concurrent/bag)

(import-clr
  [fuzz-min-int System.Math/Min : (Int Int -> Int)]
  [fuzz-max-int System.Math/Max : (Int Int -> Int)]
  [fuzz-abs-flt System.Math/Abs : (Double -> Double)]
  [fuzz-int-to-long System.Convert/ToInt64 : (Int -> Long)]
  [fuzz-long-to-int System.Convert/ToInt32 : (Long -> Int)]
  [fuzz-try-parse System.Int32/TryParse : (String -> (ValueTuple Bool Int))]
  [fuzz-abs-long System.Math/Abs : (Long -> Long)]
  [fuzz-min-long System.Math/Min : (Long Long -> Long)]
  [fuzz-max-long System.Math/Max : (Long Long -> Long)]
  [fuzz-big-mul System.Math/BigMul : (Int Int -> Long)])

(define-union (FUn_0 ^a) (Wrap_0 [value : ^a]) (Empty_0))
(@ System.ObsoleteAttribute "fuzz-deprecated")
(define-union (FUn_1 ^a) (Both_1 [a : ^a] [b : ^a]) (Neither_1))

(define-struct SRec_0 [f0 : Int] [f1 : Int] [f2 : Int])
(define-struct SRec_1 [x : Int] [y : Int])

(define-type-alias FuzzAlias System.DateTime)
(define (fuzzaliasfn [d : FuzzAlias]) : Int
  0)

(@ System.Diagnostics.DebuggerStepThroughAttribute)
(define (f0 [x15 : Int] [x16 : Int]) : Int
  (- (let ([x17 (if (equals? "oyfjfu" "\n") (+ (SRec_0/f0 (SRec_0 63 10 x16)) (treelist-fold (treelist 60 -94812 x16 x16) x15 (lambda ([x18 : Int] [x19 : Int]) 6110))) (treelist-length (treelist-filter (treelist (fold (map Nil (lambda ([x20 : Int]) -2147483648)) x15 (lambda ([x21 : Int] [x22 : Int]) 75)) (length (reverse Nil))) (lambda ([x23 : Int]) (value/0 (fuzz-try-parse "9186"))))))]) (SRec_1/y (SRec_1 (match (Cons x15 Nil) [Nil (let ([x24 : (Result Int String) (Ok -2147483648)])
    (match (map x24 (lambda ([x25 : Int]) -61069)) [(Ok x26) x15] [(Err _) x15]))] [(Cons x27 _) (* x17 71 28070)]) (let ([x29 : (Result Int String) (Ok (match 'zz ['zz 83] ['b x17] [_ 88375]))])
    (match x29 [(Ok x30) (length (concat Nil Nil))] [(Err _) (unwrap-or (flat-map (Some 10) (lambda ([x28 : Int]) (Some x15))) -75992)]))))) (aux_d805abd3_0/h1 (let ([x45 : (Result Int String) (Ok (if (> x16 -2147483648) (with-handlers ([System.InvalidOperationException x31] x15) ([System.ArithmeticException x32] 2147483647) (if #f x15 (raise (new System.InvalidOperationException "fuzz")))) (aux_d805abd3_0/h1 x15)))])
    (match (flat-map x45 (lambda ([x46 : Int]) (Ok (if (>= -75920 x16) (treelist-length (treelist-rest (treelist 2147483647 98))) (treelist-fold (treelist x46 x46) 71 (lambda ([x47 : Int] [x48 : Int]) 54)))))) [(Ok x49) (treelist-length (treelist ((lambda ([x50 : Int]) 12) x15)))] [(Err _) (treelist-length (treelist-append (treelist x16 (match (values -98491 -88897 3544 71131 2147483647) [(values x33 _ x34 x35 _) x35]) (let ([x36 : Int 89]) 11) (treelist-ref (treelist 49376 x15) 1)) (treelist (fuzz-max-int x16 x15) (let ([x37 : (Result Int String) (Ok 2147483647)])
    (match (flat-map x37 (lambda ([x38 : Int]) (Ok 75))) [(Ok x39) 86686] [(Err _) x15])) (fold (map Nil (lambda ([x40 : Int]) x40)) -89328 (lambda ([x41 : Int] [x42 : Int]) x16)) (let ([x43 : (Result Int String) (Ok 2147483647)]) (unwrap x43)) (treelist-length (treelist-map (treelist -29739 x16) (lambda ([x44 : Int]) 65))))))])))))

(define (f1 [x51 : Int] [x52 : Int]) : Int
  (% (if (= (string->symbol "x1") 'zz) (value/1 (fuzz-try-parse "3735")) (let ([x53 (length (list (match Nil [Nil x52] [(Cons x54 _) x54])))]) (length (reverse (Cons x52 Nil))))) 78))

(define (f2 [x55 : ^a] [x56 : ^b]) : ^a
  x55)

(define (vf3 [x57 : Int] [x58 : Int ...]) : Int
  x57)

(define (compute) : Int
  (with-handlers ([System.Exception x87] (string->int (int->string (aux_d805abd3_0/h0 (length (Cons 2147483647 Nil)) (treelist-first (treelist (length (list -88378 87 29)))))))) (if (contains? "wkllp" (string-append "\n" (string-append "\\" (string-append "kpwam" "ixlb")))) (match (values (match (Wrap_0 (length (append (Cons 2147483647 Nil) (use ([x59 (new System.IO.StringWriter)]) 79)))) [(Wrap_0 x60) (with-handlers ([System.InvalidOperationException x62] (with-handlers ([System.ArgumentException x63] x60) (if #t x60 (raise (new System.ArgumentException "fuzz"))))) ([System.Exception x64] (unwrap-or (flat-map (Some 51) (lambda ([x65 : Int]) (Some 31314))) x60)) (if (!= 35 x60) (match (Both_1 38 38417) [(Both_1 1 x61) x61] [Neither_1 78446] [_ 26]) (raise (new System.InvalidOperationException "fuzz"))))] [Empty_0 (let ([x68 : (Result Int String) (Ok (unwrap-or (Some 4) 6880))])
    (match x68 [(Ok x69) (if (= (symbol->string 'foo) "x1") x69 54)] [(Err _) (with-handlers ([System.Exception x66] 10) (use ([x67 (new System.IO.StringWriter)]) (if #f 2147483647 (raise (new System.InvalidOperationException "fuzz")))))]))]) (let ([x70 (typeof (Result Int String))]) 46) (let ([x71 : (Result Int String) (Ok (length (reverse (Cons 38 Nil))))]) (unwrap x71)) (length (list (* (let ([x72 : (Option (Result Int String)) (Some (Ok 13019))])
    (match x72 [(Some (Ok x73)) x73] [(Some (Err _)) -2147483648] [None 42])) (treelist-length (treelist-rest (treelist -859 2147483647 97 -63405 -2147483648)))) (treelist-length (list->treelist (Cons 46 Nil)))))) [(values x74 _ x75 x76) (unwrap-or (map (Some (if (= (string->symbol (symbol->string 'zz)) (string->symbol "x1")) (if (= (symbol->string 'b) "b") 10 x74) (use* ([x77 (new System.IO.StringWriter)] [x78 (new System.IO.MemoryStream)]) x76))) (lambda ([x81 : Int]) (let ([x83 : (Result Int String) (Ok (match (Both_1 x76 24) [(Both_1 0 x82) x76] [Neither_1 14080] [_ x76]))])
    (match (flat-map x83 (lambda ([x84 : Int]) (Ok (let ([x85 (concurrent-bag/new)]) (begin (add! x85 x74) (value/1 (try-take! x85))))))) [(Ok x86) (length (append Nil 96561))] [(Err _) (fuzz-max-int -28556 x75)])))) (fuzz-long-to-int (fuzz-min-long (fuzz-int-to-long (treelist-length (treelist-map (treelist x75 x76) (lambda ([x79 : Int]) x79)))) (fuzz-int-to-long (treelist-length (treelist-filter (treelist 2147483647) (lambda ([x80 : Int]) #f)))))))]) (raise (new System.Exception "fuzz")))))
