;; An async CLASS METHOD closes AsyncTaskMethodBuilder<> over the whole (Task T)
;; instead of T: the builder is AsyncTaskMethodBuilder<Task<Guid>> while the
;; method still declares Task<Guid>. SetResult then stores a 16-byte Guid where a
;; reference is expected.
;;
;; The same body as a module-level function unwraps correctly -- compare the
;; builder field on <Compute>d__1 (AsyncTaskMethodBuilder`1[Int32]) with the one
;; on <Get>d__0 (AsyncTaskMethodBuilder`1[Task`1[Guid]]).
;;
;; Expected 7; the IL backend returns 8, because `g` is not the Guid that was set.
(namespace ZSchemeRepro)
(module async-class-method-double-wraps-its-task-builder)

(import-clr
  [guid-parse System.Guid/Parse]
  [guid-cmp   System.Guid.CompareTo :instance : (System.Guid System.Guid -> Int)]
  System
  System.Threading.Tasks)

(define (expected) : System.Guid
  (guid-parse "11111111-1111-1111-1111-111111111111"))

(define-async (leaf) : (Task Int) 1)

(define-class Holder
  [seed : Int]
  ;; async class method that contains an await and returns a CLR value type
  (define-async (Get) : (Task System.Guid)
    (begin (await (leaf)) (expected))))

(define-async (compute) : (Task Int)
  (let ([g (await (Holder/Get (Holder 1)))])
    (+ 7 (guid-cmp g (expected)))))
