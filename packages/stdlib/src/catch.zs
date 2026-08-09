;; catch.zs -- catch macro: convert exceptions to Result<T, Error>
(module catch)

(import stdlib/result
        stdlib/error
        stdlib/option)

(import-clr
  System
  [__ex-message Exception.Message :instance-property : (Exception -> String)])

(export catch __ex-message)

(define-syntax catch
  (syntax-rules ()
    [(catch expr)
     (with-handlers
       ([System.Exception __e] (Err (Error (__ex-message __e) None)))
       (Ok expr))]))
