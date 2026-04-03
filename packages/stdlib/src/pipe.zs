;; pipe.zs — Pipe operator macro
(module pipe)

(export |>)

(define-syntax |>
  (syntax-rules ()
    [(|> x) x]
    [(|> x (f args ...) rest ...)
     (|> (f x args ...) rest ...)]
    [(|> x f rest ...)
     (|> (f x) rest ...)]))
