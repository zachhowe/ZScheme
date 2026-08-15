;; A bare CLR type name used as the type argument of an async (Task T) return
;; erases to Task<object> on the IL backend. Fatal when T is a value type: the
;; body returns an unboxed Guid into a builder closed over object.
;;
;; Spelling the same type as System.Guid compiles correctly, and a *sync*
;; `: Guid` return resolves the bare name fine — only the (Task T) argument
;; erases.
(namespace ZSchemeRepro)
(module async-task-bare-clr-type-name-erases-to-object)

(import-clr
  [new-guid System.Guid/NewGuid]
  [guid-cmp System.Guid.CompareTo :instance : (System.Guid System.Guid -> Int)]
  System
  System.Threading.Tasks)

;; `Guid` resolved through the imported `System` namespace, not written out.
(define-async (bare-result) : (Task Guid) (new-guid))

(define-async (compute) : (Task Int)
  (let ([g (await (bare-result))])
    (+ 7 (guid-cmp g g))))
