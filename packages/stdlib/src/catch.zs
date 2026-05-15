;; catch.zs -- catch macro: convert exceptions to Result<T, Error>
(module catch)

(import stdlib/result
        stdlib/error
        stdlib/option)

(import-clr
  [__ex-message System.Exception.Message :instance-property : (System.Exception -> String)])

(export catch __ex-message)

(define-syntax catch
  (syntax-rules ()
    [(catch expr)
     (with-handlers
       ([System.Exception __e] (Err (Error (__ex-message __e) None)))
       (Ok expr))]))
