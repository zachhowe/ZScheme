(namespace ZSchemeFuzzed)

(module fuzz_a8d84f43)

(import aux_a8d84f43_0)

(import stdlib/option)
(import stdlib/result)
(import stdlib/cond)
(import stdlib/concurrent/dictionary)
(import stdlib/error)
(import stdlib/catch)

(import-clr
  [fuzz-min-int System.Math/Min : (Int Int -> Int)])

(define-union (FUn_0 ^a ^b) (Left_0 [lv : ^a]) (Right_0 [rv : ^b]))

(define-interface IFuz_0
  (M0_0 [p0 : Float] : Int)
  (M0_1  : Int))

(define-class #:open FCls_0
  [f0 : Int #:mutable]
  [f1 : Int #:mutable]
  (define (M0_0 [p0 : Int]) : Int (use* ([x24 (new System.IO.MemoryStream)] [x25 (new System.IO.MemoryStream)]) (match (values (Left_0 (/ f0 58)) (match (catch (if (let* ([x26 p0] [x27 f1] [x28 33]) #f) (if (= (symbol->string 'a) "b") 98 21) (raise (new System.InvalidOperationException "fuzz-catch")))) [(Ok x29) x29] [(Err x30) f1])) [(values (Left_0 x31) x32) (* (unwrap-or (Some p0) p0) (let ([x33 936.2839]) f1) (match (catch (if #t -2147483648 (raise (new System.InvalidOperationException "fuzz-catch")))) [(Ok x34) x34] [(Err x35) p0]) (match (Some -2147483648) [(Some x36) 2147483647] [None 95]))] [_ (unwrap-or (map (Some p0) (lambda ([x37 : Int]) (unwrap-or (map (Some 66377) (lambda ([x38 : Int]) f1)) x37))) (if (= 'foo (string->symbol "foo")) 78 2147483647))])))
  (define (M0_1 [p0 : Int] [p1 : Int]) : Int (unwrap-or (flat-map (Some (match (catch (use* ([x39 (new System.IO.MemoryStream)] [x40 (new System.IO.MemoryStream)] [x41 (new System.Threading.CancellationTokenSource)]) p1)) [(Ok x42) x42] [(Err x43) (with-handlers ([System.DivideByZeroException x44] (unwrap-or (flat-map (Some 13) (lambda ([x45 : Int]) (Some 79))) 65)) ([System.AggregateException x46] (match (Some -2147483648) [(Some x47) 72340] [None f0])) ([System.NullReferenceException x48] (/ 43 -87)) ([System.FormatException x49] (let ([x50 (concurrent-dictionary/new)]) (begin (put! x50 0 p0) (value/1 (try-remove! x50 0))))) ([System.RankException x51] (match #f [#t p1] [#f f0])) ([System.FieldAccessException x52] (% p1 46)) ([System.NotImplementedException x53] (match (Some f0) [(Some x54) p0] [None -2147483648])) ([System.InvalidOperationException x55] (match (values 65917 p1 f0 f0) [(values x56 x57 _ x58) 2147483647])) ([System.InvalidCastException x59] (with-handlers ([System.ArgumentException x60] p1) ([System.DivideByZeroException x61] f0) (/ p0 (- p1 p1)))) ([System.OverflowException x62] (let ([x63 : (Result (Option Int) String) (Ok (Some f0))])
    (match x63 [(Ok (Some x64)) f0] [(Ok None) f1] [(Err _) p1]))) ([System.ArrayTypeMismatchException x65] (% 31 -50)) ([System.ArgumentNullException x66] (with-handlers ([System.InvalidOperationException x68] 58090) (with-handlers ([System.DivideByZeroException x67] p1) (if #f -2147483648 (raise (new System.InvalidOperationException "fuzz")))))) ([System.MissingMethodException x69] (let ([x70 : Int f0]) 48898)) ([System.TimeoutException x71] (let ([x72 : (Result Int String) (Ok p1)])
    (match (map x72 (lambda ([x73 : Int]) 38303)) [(Ok x74) 49791] [(Err _) p1]))) ([System.OutOfMemoryException x75] (fuzz-min-int f0 -2147483648)) ([System.NotFiniteNumberException x76] (unwrap-or (Some p0) -70230)) ([System.ArgumentOutOfRangeException x77] (match (Left_0 p1) [(Left_0 _) -2147483648] [(Right_0 x78) 2147483647])) ([System.UnauthorizedAccessException x79] (unwrap-or (flat-map (Some 60) (lambda ([x80 : Int]) (Some 2147483647))) 62)) ([System.MissingFieldException x81] (match (values f0 -892.5067) [(values 0 x82) p0] [_ 14])) ([System.MethodAccessException x83] (aux_a8d84f43_0/h1 70)) ([System.IndexOutOfRangeException x84] (match (Error/inner (Error "outer831" (Some (make-error "err915")))) [None 0] [(Some _) 1])) ([System.NotSupportedException x85] (let ([x86 : (Option (Result Int String)) (Some (Ok 2147483647))])
    (match x86 [(Some (Ok x87)) 2147483647] [(Some (Err _)) 39] [None f1]))) ([System.DuplicateWaitObjectException x88] (if (= (symbol->string 'a) "foo") 615 p1)) ([System.Exception x89] (match (Left_0 99) [(Left_0 _) p1] [(Right_0 x90) 41468])) (if (let ([x91 : (Result Int String) (Ok f1)]) (err? x91)) (let ([x92 : (Result (Option Int) String) (Ok (Some 2147483647))])
    (match x92 [(Ok (Some x93)) 99518] [(Ok None) p0] [(Err _) p0])) (raise (new System.Exception "fuzz"))))])) (lambda ([x95 : Int]) (Some (let ([x96 : IFuz_0 (object IFuz_0
  (define (M0_0 [p0 : Float]) : Int (let ([x97 (if #f 92 f1)]) (with-handlers ([System.ArgumentException x98] x97) ([System.ArithmeticException x99] p1) (/ f0 (- x97 x97)))))
  (define (M0_1) : Int (if (= (string->symbol "a") (string->symbol "a")) (match (catch 72) [(Ok x100) x100] [(Err x101) f0]) (unwrap (Some f1)))))]) (if (!= (string->symbol (symbol->string 'b)) 'b) (with-handlers ([System.InvalidOperationException x102] 97) ([System.DivideByZeroException x103] -55109) ([System.ArgumentException x104] -2147483648) ([System.Exception x105] -29621) (if #f x95 (raise (new System.ArgumentException "fuzz")))) (match (catch -2147483648) [(Ok x106) x106] [(Err x107) f1])))))) (% (let ([x94 (typeof (Result Int String))]) 34) 36))))

(define-class FCls_1 : FCls_0
  (define (M0_1 [p0 : Int] [p1 : Int]) : Int (super/M0_1 p0 p1))
  (define (M0_0 [p0 : Int]) : Int (super/M0_0 p0)))

(define (f0 [x108 : Int] [x109 : Int]) : Int
  (let ([x135 : (Result Int String) (Ok ((lambda ([x110 : Int]) (let ([x130 : (Result Int String) (Ok (match (Left_0 (with-handlers ([System.ArithmeticException x121] x109) (if #f x109 (raise (new System.DivideByZeroException "fuzz"))))) [(Left_0 1) (let ([x122 : (Result Int String) (Ok -2147483648)])
    (match (map x122 (lambda ([x123 : Int]) x108)) [(Ok x124) x124] [(Err _) x109]))] [(Right_0 0) (match -87636 [1 1] [_ x110])] [_ 85897]))])
    (match x130 [(Ok x131) (use ([x132 : System.IO.Stream (new System.IO.MemoryStream)]) (match (catch (if #t 28 (raise (new System.InvalidOperationException "fuzz-catch")))) [(Ok x133) x133] [(Err x134) x131]))] [(Err _) (let ([x127 : (Result (Option Int) String) (Ok (Some -2147483648))])
    (match x127 [(Ok (Some x128)) (match (Right_0 15) [(Left_0 _) 8195] [(Right_0 x129) -2147483648])] [(Ok None) (use* ([x125 (new System.IO.MemoryStream)] [x126 (new System.IO.MemoryStream)]) x108)] [(Err _) (cond [#f x108] [else x108])]))]))) (match (catch (let ([x112 : (Result Int String) (Ok (float->int (int->float 52416)))])
    (match (flat-map x112 (lambda ([x113 : Int]) (Ok (with-handlers ([System.InvalidOperationException x115] x113) (with-handlers ([System.DivideByZeroException x114] 55) (if #f x109 (raise (new System.DivideByZeroException "fuzz")))))))) [(Ok x116) (if (= (symbol->string 'x1) "x1") x109 x116)] [(Err _) (match (Left_0 -97953) [(Left_0 _) x109] [(Right_0 x111) 97])]))) [(Ok x117) x117] [(Err x118) (if (= (float->double (+ 11.22886 220.4423)) (float->double (+ 11.22886 220.4423))) (let* ([x119 x108] [x120 x119]) x108) (unwrap-or (Some -56025) 23))])))]) (unwrap x135)))

(define (f1 [x136 : Int] [x137 : Int]) : Int
  (if (<= x136 0) (match (catch (if (let ([x140 : (Result Int String) (Ok (match (catch -2147483648) [(Ok x138) x138] [(Err x139) x137]))]) (err? x140)) (float->int (int->float 845079)) (raise (new System.InvalidOperationException "fuzz-catch")))) [(Ok x141) x141] [(Err x142) (aux_a8d84f43_0/h0 (if (!= (string->symbol (symbol->string 'zz)) 'foo) 57 x137) (unwrap-or (map (Some x136) (lambda ([x143 : Int]) 53)) 44))]) (+ 1 (f1 (- x136 1) x137))))

(define (fuzz-run-func [f : (delegate System.Func<int,int>)]) : Int
  (f 10))

(define (fuzz-run-action [a : (delegate System.Action)]) : Unit
  (a))

(define (fuzz-deleg-fn [x : Int]) : Int
  (* x 2))

(define-async (g0 [x144 : Int] [x145 : Int]) : (Task Int)
  (fuzz-run-func (lambda ([x146 : Int]) (with-handlers ([System.InvalidOperationException x148] (if (= (symbol->string 'zz) "foo") (let ([x151 : (Option (Option Int)) (Some (Some (let ([x149 -780.9609]) x144)))])
    (match x151 [(Some (Some x152)) (let ([x153 (typeof (Result Int String))]) 68)] [(Some None) (let ([x150 : (Result Int String) (Ok 46)]) (unwrap x150))] [None 76])) (aux_a8d84f43_0/h0 (let ([x154 (typeof Byte)]) 21) (let* ([x155 -49525] [x156 x144]) x146)))) ([System.ArgumentException x157] (+ ((lambda ([x158 : Int]) (% 43 76)) ((lambda ([x159 : Int]) x159) -2147483648)) (if (> x145 53 x146 x145) (match (catch (if #f 9608 (raise (new System.InvalidOperationException "fuzz-catch")))) [(Ok x160) x160] [(Err x161) 28]) (fuzz-min-int x145 x144)))) ([System.DivideByZeroException x162] (string->int (int->string (use ([x163 : System.IO.Stream (new System.IO.MemoryStream)]) (/ 49 81))))) ([System.Exception x164] (match (values (f0 (with-handlers ([System.Exception x165] 2) (use ([x166 (new System.Threading.CancellationTokenSource)]) (if #f 25490 (raise (new System.InvalidOperationException "fuzz"))))) (match (Error/inner (make-error "err391")) [None 0] [(Some _) 1])) (float->int (int->float 444378)) (match (string->symbol (symbol->string 'my-sym)) ['my-sym (match (catch (if #t x146 (raise (new System.InvalidOperationException "fuzz-catch")))) [(Ok x167) x167] [(Err x168) 91])] ['foo (with-handlers ([System.Exception x169] x146) (use ([x170 (new System.IO.StringWriter)]) (if #f x144 (raise (new System.InvalidOperationException "fuzz")))))] ['x1 (/ 87 35)] [_ (let ([x171 : (Result (Option Int) String) (Ok (Some x144))])
    (match x171 [(Ok (Some x172)) x146] [(Ok None) x146] [(Err _) x144]))]) (f1 4 -2147483648) (+ x145 (if (= (float->double -321.045) (float->double -0.0)) -46940 16)) (fuzz-min-int (match (catch -73320) [(Ok x173) x173] [(Err x174) 2147483647]) (let ([x175 : Int x145]) 93))) [(values x176 _ x177 _ _ 3) (with-handlers ([System.Exception x178] (let ([x179 (string->symbol "my-sym")]) (if (= x179 (string->symbol (symbol->string 'my-sym))) -2147483648 46))) (use ([x180 (new System.IO.StringWriter)]) (if #t (fuzz-run-func fuzz-deleg-fn) (raise (new System.InvalidOperationException "fuzz")))))] [_ (use* ([x181 (new System.IO.StringWriter)] [x182 (new System.IO.MemoryStream)] [x183 (new System.IO.MemoryStream)]) (use ([x184 (new System.IO.StringWriter)]) x145))])) (/ (f1 19 (use ([x147 (new System.IO.StringWriter)]) (fuzz-min-int -30837 x145))) (- x145 x145))))))

(define-async (g1 [x185 : Int]) : (Task Int)
  (with-handlers ([System.Exception x186] (await (g0 (match (catch (let ([x187 : IFuz_0 (object IFuz_0
  (define (M0_0 [p0 : Float]) : Int (with-handlers ([System.InvalidOperationException x189] 22) (with-handlers ([System.DivideByZeroException x188] 52) (if #f 91 (raise (new System.DivideByZeroException "fuzz"))))))
  (define (M0_1) : Int (let ([x190 : (Result Int String) (Ok 100)])
    (match (flat-map x190 (lambda ([x191 : Int]) (Ok x191))) [(Ok x192) x192] [(Err _) 81]))))]) (+ x185 -2147483648 x185 x185))) [(Ok x193) x193] [(Err x194) (if (and #t #f) 18 (float->int (int->float -745534)))]) (let ([x195 : (Result Int String) (Ok x185)]) (unwrap x195))))) (if (some? (Some (% (f0 (let ([x196 (concurrent-dictionary/new)]) (begin (put! x196 0 4527) (value/1 (try-remove! x196 0)))) (use ([x197 (new System.IO.StringWriter)]) 78928)) 40))) (with-handlers ([System.InvalidOperationException x199] (use* ([x207 (new System.Threading.CancellationTokenSource)] [x208 (new System.IO.StringWriter)] [x209 (new System.IO.MemoryStream)]) (if (= (symbol->string 'a) "b") (if (= (symbol->string 'zz) "zz") 31 x185) (let ([x210 : (Option (Result (Option Int) String)) (Some (Ok (Some x185)))])
    (match x210 [(Some (Ok (Some x211))) 78] [(Some (Ok None)) -441] [(Some (Err _)) x185] [None 65]))))) (with-handlers ([System.DivideByZeroException x198] (match (catch ((partial f0 (begin (new FCls_1 90 20) 7)) (use* ([x200 (new System.IO.StringWriter)] [x201 (new System.Threading.CancellationTokenSource)] [x202 (new System.Threading.CancellationTokenSource)]) 84))) [(Ok x203) x203] [(Err x204) (let ([x205 : (Result Int String) (Ok (if (= (symbol->string 'a) "zz") x185 x185))])
    (match x205 [(Ok x206) (fuzz-min-int 6 82)] [(Err _) (if (= (float->double 61.84154) (float->double 61.84154)) x185 69)]))])) (if (or (let ([x215 : (Result Int String) (Ok (let ([x212 : (Result Int String) (Ok x185)])
    (match (map x212 (lambda ([x213 : Int]) 24)) [(Ok x214) -2147483648] [(Err _) -34552])))]) (ok? x215)) #f (<= 737.9011 1.0)) (match (Some (let ([x217 : (Result Int String) (Ok (* 6 x185))])
    (match (flat-map x217 (lambda ([x218 : Int]) (Ok (fuzz-min-int -2147483648 x185)))) [(Ok x219) (let* ([x220 x185] [x221 -2147483648]) 79)] [(Err _) (match (values 53024 0.0) [(values 2 x216) 44] [_ 14996])]))) [(Some x223) (use ([x224 (new System.IO.StringWriter)]) (let ([x225 : String "λ"]) 63993))] [None (- (if (= (symbol->string 'foo) "x1") x185 x185) (let ([x222 (concurrent-dictionary/new)]) (begin (put! x222 0 27546) (value/1 (try-remove! x222 0)))))]) (raise (new System.DivideByZeroException "fuzz"))))) (raise (new System.Exception "fuzz")))))

(define (compute) : Int
  (string->int (int->string (aux_a8d84f43_0/h2 (let ([x232 : (Result Int String) (Ok (with-handlers ([System.Exception x226] (if (= "\n" "💩") 1 0)) (use ([x227 (new System.Threading.CancellationTokenSource)]) (if (> 67732 24 -5931 71) (let ([x228 : (Result Int String) (Ok 73)])
    (match x228 [(Ok x229) 36] [(Err _) 100])) (raise (new System.InvalidOperationException "fuzz"))))))])
    (match x232 [(Ok x233) (unwrap-or (map (Some (let* ([x234 33] [x235 (+ x234 25)]) 2147483647)) (lambda ([x236 : Int]) (let ([x237 : (Result Int String) (Ok x236)]) (unwrap x237)))) (match "iigvfx" ["fuzz" 21] ["hello" 45] ["abc" x233] [_ 32]))] [(Err _) (let ([x230 : IFuz_0 (object IFuz_0
  (define (M0_0 [p0 : Float]) : Int 54046)
  (define (M0_1) : Int (unwrap-or (map (Some 2147483647) (lambda ([x231 : Int]) 48)) 67990)))]) 7)]))))))
