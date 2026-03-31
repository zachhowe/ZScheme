;; string.zs — String utilities
(module string)

(import-clr
  [clr-format System.String/Format : (Fn [String (Mutable-Array Object)] String)])

(define (format [fmt : String] [args : Object ...]) : String
  (clr-format fmt args))

(export format)
