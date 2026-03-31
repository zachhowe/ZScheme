;; string.zs — String utilities
(module string)

(import-clr
  [clr-format System.String/Format : (Fn [String (Mutable-Array String)] String)])

(define (string/format [fmt : String] [args : String ...]) : String
  (clr-format fmt args))

(export string/format)
