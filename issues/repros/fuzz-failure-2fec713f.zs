(namespace ZSchemeFuzzed)

(module fuzz_2fec713f)

(import stdlib/string)
(import stdlib/concurrent/stack)

(import-clr
  [fuzz-abs-int System.Math/Abs : (Int -> Int)]
  [fuzz-str-len System.String.Length :instance-property : (String -> Int)]
  [fuzz-try-parse System.Int32/TryParse : (String -> (ValueTuple Bool Int))])

(define-record (FRec_0 ^a) [x : ^a] [y : ^a])

(define-struct SRec_0 [x : Int] [y : Int])
(define-struct SRec_1 [x : Int] [y : Int])

(define-interface IFuz_0
  (M0_0 [p0 : Float] [p1 : Int] : Int)
  (M0_1 [p0 : Int] : Int))

(define-class #:open FCls_0
  [f0 : Int #:mutable]
  (define (M0_0) : Int (with-handlers ([System.InvalidOperationException x1] (with-handlers ([System.Exception x2] (let ([x3 'x1]) (if (= x3 (string->symbol (symbol->string 'x1))) (match 'my-sym ['my-sym f0] ['x1 44] [_ f0]) f0))) (use ([x4 (new System.IO.MemoryStream)]) (if (empty? "oewwh") (let ([f0 (match (values f0 100 55 90 -86712 f0 f0) [(values 2 x5 f0 _ x6 _ x7) 46] [_ 44])]) (let ([x8 : IFuz_0 (object IFuz_0
  (define (M0_0 [p0 : Float] [p1 : Int]) : Int p1)
  (define (M0_1 [p0 : Int]) : Int 10))]) 17224)) (raise (new System.InvalidOperationException "fuzz")))))) (with-handlers ([System.DivideByZeroException x0] (if (empty? "\"") (fuzz-abs-int (let ([x60 (concurrent-stack/new)]) (begin (push! x60 ((lambda ([x61 : Int]) 35356) 42)) (push! x60 (- 12 -2147483648)) (push! x60 (with-handlers ([System.Exception x62] f0) (use ([x63 (new System.Threading.CancellationTokenSource)]) (if #t f0 (raise (new System.InvalidOperationException "fuzz")))))) (length x60)))) (raise (new System.InvalidOperationException "fuzz")))) (if (or (contains? "\\" "\t") (if (> -130.196 0.0 -958.9713) (starts-with? "\t" "\"") (and #t #f))) (with-handlers ([System.InvalidOperationException x9] (with-handlers ([System.OutOfMemoryException x10] (match (values -2147483648 f0) [(values x11 4) 68009] [_ -9812])) ([System.NotFiniteNumberException x12] (% -2147483648 -1)) ([System.InvalidCastException x13] ((lambda ([x14 : Int]) f0) -19586)) ([System.OverflowException x15] (let* ([f0 f0] [x16 (+ f0 f0)]) 53)) ([System.NotSupportedException x17] (match (values -2147483648 24) [(values x18 f0) f0])) ([System.FormatException x19] (let ([x20 "fdvw"]) f0)) ([System.TimeoutException x21] (+ 38 f0)) ([System.MissingFieldException x22] (begin f0 67 #t -2147483648)) ([System.ArgumentNullException x23] (let* ([f0 f0] [x24 (+ f0 f0)] [f0 f0]) x24)) ([System.RankException x25] (match (string->symbol (symbol->string 'x1)) ['x1 19] ['a f0] ['b f0] [_ f0])) ([System.NullReferenceException x26] (with-handlers ([System.DivideByZeroException x27] f0) ([System.InvalidOperationException x28] 29) ([System.ArgumentException x29] -68497) (if #f f0 (raise (new System.DivideByZeroException "fuzz"))))) ([System.AggregateException x30] (% f0 -10)) ([System.MethodAccessException x31] (match #t [#t 35] [#f f0])) ([System.DuplicateWaitObjectException x32] (begin #f -698.9095 722.5423 55)) ([System.ArrayTypeMismatchException x33] (with-handlers ([System.Exception x34] 55) (use ([x35 (new System.IO.MemoryStream)]) (if #f f0 (raise (new System.InvalidOperationException "fuzz")))))) ([System.MissingMethodException x36] (fuzz-abs-int 2147483647)) ([System.UnauthorizedAccessException x37] (% 18 -49)) ([System.DivideByZeroException x38] (fuzz-abs-int f0)) ([System.IndexOutOfRangeException x39] (let ([x40 0.0]) -96946)) ([System.FieldAccessException x41] (if (= "\"" "tq") 1 0)) ([System.ArgumentOutOfRangeException x42] (let* ([x43 -2147483648] [x44 2147483647] [x45 (+ x43 x43)]) -2147483648)) ([System.InvalidOperationException x46] (use ([x47 : System.IO.Stream (new System.IO.MemoryStream)]) 96)) ([System.NotImplementedException x48] (let ([x49 (typeof Byte)]) 91)) ([System.Exception x50] (with-handlers ([System.ArgumentException x51] f0) (if #t f0 (raise (new System.ArgumentException "fuzz"))))) (if (> f0 f0) (% f0 -84) (raise (new System.Exception "fuzz"))))) ([System.DivideByZeroException x52] (with-handlers ([System.InvalidOperationException x54] (let* ([x55 84068] [x55 f0] [f0 (+ x55 99)]) f0)) (with-handlers ([System.DivideByZeroException x53] (fuzz-abs-int 44)) (if (let* ([f0 81] [x56 (+ f0 f0)]) #t) (let ([x57 (concurrent-stack/new)]) (begin (push! x57 49843) (length x57))) (raise (new System.DivideByZeroException "fuzz")))))) ([System.Exception x58] (string->int (int->string ((lambda ([x59 : Int]) f0) 1)))) (/ 17 (- f0 f0))) (raise (new System.DivideByZeroException "fuzz"))))))
  (define (M0_1 [p0 : Int] [p1 : Int]) : Int (* (let ([x64 : IFuz_0 (object IFuz_0
  (define (M0_0 [p0 : Float] [p1 : Int]) : Int (if (if #t #f #t) (string->int (int->string -2147483648)) (SRec_0/y (SRec_0 p1 p1))))
  (define (M0_1 [p0 : Int]) : Int (with-handlers ([System.NotSupportedException x65] (if (= "\"" "yd") 1 0)) ([System.UnauthorizedAccessException x66] (float->int (int->float 146134))) ([System.MissingMethodException x67] (fuzz-abs-int 2147483647)) ([System.ArgumentOutOfRangeException x68] (value/1 (fuzz-try-parse "xxda"))) ([System.FormatException x69] (fuzz-str-len "")) ([System.IndexOutOfRangeException x70] (let ([x71 (typeof (FRec_0 Int))]) 86)) ([System.ArgumentNullException x72] (let ([x73 (string->symbol "a")]) (if (= x73 (string->symbol (symbol->string 'a))) p0 p0))) ([System.ArrayTypeMismatchException x74] (let* ([x75 f0] [x76 (+ x75 29)]) x76)) ([System.MethodAccessException x77] (let ([x78 #f]) 30)) ([System.AggregateException x79] (FRec_0/y (FRec_0 11 p1))) ([System.RankException x80] (if #t -71424 30)) ([System.TimeoutException x81] 2) ([System.InvalidCastException x82] (with-handlers ([System.InvalidOperationException x83] f0) ([System.DivideByZeroException x84] p1) (if #f -2147483648 (raise (new System.DivideByZeroException "fuzz"))))) ([System.NotFiniteNumberException x85] (% 22 40)) ([System.MissingFieldException x86] (with-handlers ([System.InvalidOperationException x88] p1) (with-handlers ([System.DivideByZeroException x87] (if #t -2147483648 (raise (new System.InvalidOperationException "fuzz")))) (if #f 6 (raise (new System.DivideByZeroException "fuzz")))))) ([System.NotImplementedException x89] (let* ([x90 p1] [p0 (+ x90 f0)]) p0)) ([System.FieldAccessException x91] (+ 45459 p1)) ([System.OverflowException x92] (match (values 1 p0) [(values x93 x94) f0])) ([System.DuplicateWaitObjectException x95] (match (string->symbol "b") ['b p1] [_ 2147483647])) ([System.DivideByZeroException x96] (if #t p0 p1)) ([System.OutOfMemoryException x97] (fuzz-abs-int -88480)) ([System.NullReferenceException x98] (+ -40140 f0)) ([System.InvalidOperationException x99] (match (values 98 p1 p0) [(values f0 p0 _) p1])) ([System.Exception x100] (string->int (int->string p1))) (if (< 0.0 -1.0) (+ p1 f0 f0 p0) (raise (new System.Exception "fuzz"))))))]) (SRec_1/x (SRec_1 (SRec_1/y (with (SRec_1 p0 f0) [x p1])) (use ([x101 : System.IO.Stream (new System.IO.MemoryStream)]) 54)))) (let ([x102 (string->symbol "x1")]) (if (= x102 (string->symbol "x1")) (with-handlers ([System.Exception x103] (with-handlers ([System.InvalidOperationException x105] 77) (with-handlers ([System.DivideByZeroException x104] (if #f f0 (raise (new System.InvalidOperationException "fuzz")))) (if #f 37 (raise (new System.DivideByZeroException "fuzz")))))) (if (>= 2147483647 -58227 14193) (begin p0 -899.182 #t p1) (raise (new System.Exception "fuzz")))) (with-handlers ([System.ArithmeticException x107] (match 779.5256 [-0.0 p1] [2.5 f0] [_ 48])) ([System.Exception x108] (with-handlers ([System.InvalidOperationException x110] 57917) (with-handlers ([System.DivideByZeroException x109] (if #t p0 (raise (new System.InvalidOperationException "fuzz")))) (if #f -42158 (raise (new System.DivideByZeroException "fuzz")))))) (if (equals? "ef" "ymsga") (match (SRec_1 f0 p1) [(SRec_1 _ x106) 72]) (raise (new System.Exception "fuzz")))))))))

(define-class FCls_1 : FCls_0
  [d0 : Int #:mutable]
  (define (M0_1 [p0 : Int] [p1 : Int]) : Int (+ (super/M0_1 p0 p1) d0)))

(import-clr
  [call-c0-m0-0 ZSchemeFuzzed.FCls_0.M0_0 :instance : (FCls_0 -> Int)]
  [call-c0-m0-1 ZSchemeFuzzed.FCls_0.M0_1 :instance : (FCls_0 Int Int -> Int)]
  [call-c1-m0-1 ZSchemeFuzzed.FCls_1.M0_1 :instance : (FCls_1 Int Int -> Int)])

(define (f0 [x111 : (Int -> Int)] [x112 : Int]) : Int
  (if (= (float->double (- 231.0627 (+ -242.8835 (* (- 0.0 -1.0 858.4492 297.3927) (/ 641.6349 995.4297 0.0 505.5273 -0.0))) -77.71759 (- -209.3715 (* (int->float 64992) (+ 1.0 254.7984))) 14.91162)) (float->double (- 231.0627 (+ -242.8835 (* (- 0.0 -1.0 858.4492 297.3927) (/ 641.6349 995.4297 0.0 505.5273 -0.0))) -77.71759 (- -209.3715 (* (int->float 64992) (+ 1.0 254.7984))) 14.91162))) (string->int (int->string x112)) (* ((lambda ([x113 : Int]) (with-handlers ([System.InvalidOperationException x115] (if (empty? (format "x{0}" "\\")) 1 0)) (with-handlers ([System.DivideByZeroException x114] (with-handlers ([System.Exception x116] x112) (use ([x117 (new System.IO.MemoryStream)]) (if #t 2147483647 (raise (new System.InvalidOperationException "fuzz")))))) (if (contains? "\"" "\"") (begin 2147483647 84066 x112) (raise (new System.InvalidOperationException "fuzz")))))) x112) (float->int (int->float -102031)))))

(define (f1 [x118 : ^a] [x119 : ^b]) : ^a
  x118)

(define (vf2 [x120 : Int] [x121 : Int ...]) : Int
  x120)

(define (fuzz-run-func [f : (delegate System.Func<int,int>)]) : Int
  (f 10))

(define (fuzz-run-action [a : (delegate System.Action)]) : Unit
  (a))

(define (fuzz-deleg-fn [x : Int]) : Int
  (* x 2))

(define-async (g0 [x122 : Int] [x123 : Int]) : (Task Int)
  x122)

(define-async (g1 [x124 : Int] [x125 : Int]) : (Task Int)
  (with-handlers ([System.Exception x126] (await (g0 30 (match "\\" ["fuzz" (if (!= (float->double -558.2751) (float->double -1.0)) (let ([x124 : Int x125]) x124) (let ([x127 (new FCls_0 x124)])
    (call-c0-m0-0 x127)))] ["hello" (let ([x128 'my-sym]) (if (= x128 (string->symbol "my-sym")) (let ([x129 (concurrent-stack/new)]) (begin (push! x129 2147483647) (push! x129 13) (push! x129 x125) (length x129))) (if (= "xptt" "\t") 1 0)))] [_ (let ([x130 (concurrent-stack/new)]) (begin (push! x130 (if (= (float->double -0.0) (float->double -1.0)) x125 x125)) (value/1 (try-pop! x130))))])))) (if (< (int->float (FRec_0/x (FRec_0 (match #t [#t 4] [#f 8043]) (/ -85946 94 29)))) (/ (int->float (match (values -56408 -802.1697) [(values _ x131) -2939])) (+ (- -116.3551 -357.8933) (+ 800.9035 1.0)) -445.9353 (double->float (float->double -123.8436)) (- -355.2777 (/ -42.47384 -315.5324) -341.5013))) (with-handlers ([System.InvalidOperationException x135] (if (= (float->double (int->float (begin (fuzz-run-action (lambda () ())) 56))) (float->double (int->float (begin (fuzz-run-action (lambda () ())) 56)))) (if (= (symbol->string 'b) "my-sym") (let ([x136 (typeof (Double * Bool * Int))]) 6) (fuzz-str-len "\\")) (with-handlers ([System.DivideByZeroException x137] (let* ([x138 x124] [x124 (+ x138 58)]) 92)) ([System.InvalidOperationException x139] (let ([x140 (concurrent-stack/new)]) (begin (push! x140 x124) (push! x140 -62435) (push! x140 x124) (length x140)))) ([System.ArgumentException x141] (use ([x142 : System.IO.Stream (new System.IO.MemoryStream)]) x124)) ([System.Exception x143] (begin (fuzz-run-action (lambda () ())) x124)) (/ (+ 28 x125 x125 x124) (- x124 x124))))) ([System.ArithmeticException x144] (if (= (string-append (string-append "frldu" "blqli") "ehchd") "\n") 1 0)) ([System.ArgumentException x145] (fuzz-str-len "\n")) (/ (SRec_0/x (SRec_0 (let ([x125 (use ([x132 : System.IO.Stream (new System.IO.MemoryStream)]) x125)]) ((lambda ([x133 : Int]) 29) x125)) (string->int (int->string (match (values x125 103.8523) [(values _ x134) x124]))))) (- x125 x125))) (raise (new System.Exception "fuzz")))))

(define (compute) : Int
  83)
