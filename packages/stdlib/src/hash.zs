;; hash.zs — Hash operations via ImmutableDictionary<K,V>
(module hash)

(import stdlib/option)
;; Pull in the Mutable-Vector alias so hash-create-range's signature
;; (taking Mutable-Vector (Pair ^k ^v)) and variadic functions resolve.
(import stdlib/mutable/vector)

;; Map the ZScheme name `Hash` to System.Collections.Immutable.ImmutableDictionary<K,V> at codegen.
(define-type-alias (Hash ^k ^v)
  System.Collections.Immutable.ImmutableDictionary :from "System.Collections.Immutable")

;; Map the ZScheme name `Pair` to System.Collections.Generic.KeyValuePair<K,V> at codegen.
(define-type-alias (Pair ^k ^v)
  System.Collections.Generic.KeyValuePair :from "System.Collections.Generic")

;; Mutable-Hash and TreeList are referenced below but their canonical declarations live in
;; stdlib/mutable/hash (mutual Hash<->Mutable-Hash cycle) and stdlib/treelist (not imported).
;; Re-declare locally — must mirror the canonical targets exactly.
(define-type-alias (Mutable-Hash ^k ^v) System.Collections.Generic.Dictionary)
(define-type-alias (TreeList ^a)
  System.Collections.Immutable.ImmutableList :from "System.Collections.Immutable")

;; CLR bindings (internal)
(import-clr
  System.Collections.Immutable
  System.Collections.Generic
  [pair-create System.Collections.Generic.KeyValuePair/Create ^k ^v
    : (^k ^v -> (Pair ^k ^v))]
  [hash-create-range System.Collections.Immutable.ImmutableDictionary/CreateRange ^k ^v
    : ((Mutable-Vector (Pair ^k ^v)) -> (Hash ^k ^v))]
  [hash-count-raw System.Collections.Immutable.ImmutableDictionary.Count
    :instance-property : ((Hash ^k ^v) -> Int)]
  [hash-item-raw System.Collections.Immutable.ImmutableDictionary.Item
    :instance-indexer : ((Hash ^k ^v) ^k -> ^v)]
  [hash-set-raw System.Collections.Immutable.ImmutableDictionary.SetItem
    :instance : ((Hash ^k ^v) ^k ^v -> (Hash ^k ^v))]
  [hash-remove-raw System.Collections.Immutable.ImmutableDictionary.Remove
    :instance : ((Hash ^k ^v) ^k -> (Hash ^k ^v))]
  [hash-contains-raw System.Collections.Immutable.ImmutableDictionary.ContainsKey
    :instance : ((Hash ^k ^v) ^k -> Bool)]
  ;; dict-keys/dict-values return IEnumerable at CLR level but are annotated as
  ;; TreeList to satisfy ZScheme's type system. Only safe when passed to create-treelist-from.
  [dict-keys System.Collections.Immutable.ImmutableDictionary.Keys
    :instance-property : ((Hash ^k ^v) -> (TreeList ^k))]
  [dict-values System.Collections.Immutable.ImmutableDictionary.Values
    :instance-property : ((Hash ^k ^v) -> (TreeList ^v))]
  [create-treelist-from System.Collections.Immutable.ImmutableList/CreateRange ^a
    : ((TreeList ^a) -> (TreeList ^a))]
  [hash-from-mutable-raw System.Collections.Immutable.ImmutableDictionary/CreateRange ^k ^v
    : ((Mutable-Hash ^k ^v) -> (Hash ^k ^v))])

;; Constructors

(define (pair [k : ^k] [v : ^v]) : (Pair ^k ^v)
  :where (^k notnull)
  (pair-create k v))

(define (hash [entries : (Pair ^k ^v) ...]) : (Hash ^k ^v)
  :where (^k notnull)
  (hash-create-range entries))

;; Exported functions

(define (hash-count [h : (Hash ^k ^v)]) : Int
  :where (^k notnull)
  (hash-count-raw h))

(define (hash-set [h : (Hash ^k ^v)] [key : ^k] [val : ^v]) : (Hash ^k ^v)
  :where (^k notnull)
  (hash-set-raw h key val))

(define (hash-remove [h : (Hash ^k ^v)] [key : ^k]) : (Hash ^k ^v)
  :where (^k notnull)
  (hash-remove-raw h key))

(define (hash-has-key? [h : (Hash ^k ^v)] [key : ^k]) : Bool
  :where (^k notnull)
  (hash-contains-raw h key))

(define (hash-empty? [h : (Hash ^k ^v)]) : Bool
  :where (^k notnull)
  (= (hash-count-raw h) 0))

(define (hash-ref [h : (Hash ^k ^v)] [key : ^k]) : (Option ^v)
  :where (^k notnull)
  (if (hash-contains-raw h key)
    (Some (hash-item-raw h key))
    None))

(define (hash-keys [h : (Hash ^k ^v)]) : (TreeList ^k)
  :where (^k notnull)
  (create-treelist-from (dict-keys h)))

(define (hash-values [h : (Hash ^k ^v)]) : (TreeList ^v)
  :where (^k notnull)
  (create-treelist-from (dict-values h)))

;; Conversions

;; Mutable-Hash -> Hash via ImmutableDictionary.CreateRange<K,V>(IEnumerable<KeyValuePair<K,V>>).
(define (mutable-hash->hash [h : (Mutable-Hash ^k ^v)]) : (Hash ^k ^v)
  :where (^k notnull)
  (hash-from-mutable-raw h))

(export pair hash hash-count hash-set hash-remove hash-has-key? hash-empty? hash-ref hash-keys hash-values
        mutable-hash->hash)
