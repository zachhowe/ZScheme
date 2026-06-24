;; mutable-hash.zs — Mutable-Hash operations via Dictionary<K,V>
(module mutable-hash)

(import stdlib/option)

;; Map the ZScheme name `Mutable-Hash` to System.Collections.Generic.Dictionary<K,V> at codegen.
(define-type-alias (Mutable-Hash ^k ^v) System.Collections.Generic.Dictionary)

;; Hash and TreeList are referenced below but their canonical declarations live in
;; stdlib/hash (mutual Hash<->Mutable-Hash cycle) and stdlib/treelist (not imported).
;; Re-declare locally — must mirror the canonical targets exactly.
(define-type-alias (Hash ^k ^v)
  System.Collections.Immutable.ImmutableDictionary :from "System.Collections.Immutable")
(define-type-alias (TreeList ^a)
  System.Collections.Immutable.ImmutableList :from "System.Collections.Immutable")

;; CLR bindings (internal)
(import-clr
  System.Collections.Generic
  System.Collections.Immutable
  [mh-count-raw System.Collections.Generic.Dictionary.Count
    :instance-property : ((Mutable-Hash ^k ^v) -> Int)]
  [mh-item-raw System.Collections.Generic.Dictionary.Item
    :instance-indexer : ((Mutable-Hash ^k ^v) ^k -> ^v)]
  [mh-set-item-raw System.Collections.Generic.Dictionary.Item
    :instance-indexer-set : ((Mutable-Hash ^k ^v) ^k ^v -> Unit)]
  [mh-contains-key-raw System.Collections.Generic.Dictionary.ContainsKey
    :instance : ((Mutable-Hash ^k ^v) ^k -> Bool)]
  [mh-remove-raw System.Collections.Generic.Dictionary.Remove
    :instance : ((Mutable-Hash ^k ^v) ^k -> Bool)]
  [mh-clear-raw System.Collections.Generic.Dictionary.Clear
    :instance : ((Mutable-Hash ^k ^v) -> Unit)]
  ;; Dictionary.Keys/.Values return KeyCollection/ValueCollection, which are IEnumerable<T>
  ;; (Seq); create-list-from materializes them into a TreeList via ImmutableList.CreateRange.
  [mh-keys-raw System.Collections.Generic.Dictionary.Keys
    :instance-property : ((Mutable-Hash ^k ^v) -> (Seq ^k))]
  [mh-values-raw System.Collections.Generic.Dictionary.Values
    :instance-property : ((Mutable-Hash ^k ^v) -> (Seq ^v))]
  [create-list-from System.Collections.Immutable.ImmutableList/CreateRange ^a
    : ((Seq ^a) -> (TreeList ^a))])

;; Exported functions

;; Create an empty mutable hash
(define (make-hash) : (Mutable-Hash ^k ^v)
  :where (^k notnull)
  (new (System.Collections.Generic.Dictionary ^k ^v)))

(define (hash-count [h : (Mutable-Hash ^k ^v)]) : Int
  :where (^k notnull)
  (mh-count-raw h))

(define (hash-set! [h : (Mutable-Hash ^k ^v)] [key : ^k] [val : ^v]) : Unit
  :where (^k notnull)
  (mh-set-item-raw h key val))

(define (hash-ref [h : (Mutable-Hash ^k ^v)] [key : ^k]) : (Option ^v)
  :where (^k notnull)
  (if (mh-contains-key-raw h key)
    (Some (mh-item-raw h key))
    None))

(define (hash-remove! [h : (Mutable-Hash ^k ^v)] [key : ^k]) : Bool
  :where (^k notnull)
  (mh-remove-raw h key))

(define (hash-has-key? [h : (Mutable-Hash ^k ^v)] [key : ^k]) : Bool
  :where (^k notnull)
  (mh-contains-key-raw h key))

(define (hash-clear! [h : (Mutable-Hash ^k ^v)]) : Unit
  :where (^k notnull)
  (mh-clear-raw h))

(define (hash-empty? [h : (Mutable-Hash ^k ^v)]) : Bool
  :where (^k notnull)
  (= (mh-count-raw h) 0))

(define (hash-keys [h : (Mutable-Hash ^k ^v)]) : (TreeList ^k)
  :where (^k notnull)
  (create-list-from (mh-keys-raw h)))

(define (hash-values [h : (Mutable-Hash ^k ^v)]) : (TreeList ^v)
  :where (^k notnull)
  (create-list-from (mh-values-raw h)))

;; Conversions

;; Hash -> Mutable-Hash by constructing a new Dictionary from the immutable view.
;; Matches Racket's hash-copy: produces a mutable copy of any hash.
(define (hash-copy [h : (Hash ^k ^v)]) : (Mutable-Hash ^k ^v)
  :where (^k notnull)
  (new (System.Collections.Generic.Dictionary ^k ^v) h))

(export make-hash hash-count hash-set! hash-ref hash-remove!
        hash-has-key? hash-clear! hash-empty? hash-keys hash-values
        hash-copy)
