;; map.zs — Map operations via ImmutableDictionary<K,V>
(module map)

(import stdlib/option)
;; Pull in the Mutable-Array alias so map-create-range's signature
;; (taking Mutable-Array (Pair ^k ^v)) and variadic functions resolve.
(import stdlib/mutable/array)

;; Map the ZScheme name `Map` to System.Collections.Immutable.ImmutableDictionary<K,V> at codegen.
(define-type-alias (Map ^k ^v)
  System.Collections.Immutable.ImmutableDictionary :from "System.Collections.Immutable")

;; Map the ZScheme name `Pair` to System.Collections.Generic.KeyValuePair<K,V> at codegen.
(define-type-alias (Pair ^k ^v)
  System.Collections.Generic.KeyValuePair :from "System.Collections.Generic")

;; CLR bindings (internal)
(import-clr
  System.Collections.Immutable
  System.Collections.Generic
  [pair-create System.Collections.Generic.KeyValuePair/Create ^k ^v
    : (^k ^v -> (Pair ^k ^v))]
  [map-create-range System.Collections.Immutable.ImmutableDictionary/CreateRange ^k ^v
    : ((Mutable-Array (Pair ^k ^v)) -> (Map ^k ^v))]
  [map-count-raw System.Collections.Immutable.ImmutableDictionary.Count
    :instance-property : ((Map ^k ^v) -> Int)]
  [map-item-raw System.Collections.Immutable.ImmutableDictionary.Item
    :instance-indexer : ((Map ^k ^v) ^k -> ^v)]
  [map-set-raw System.Collections.Immutable.ImmutableDictionary.SetItem
    :instance : ((Map ^k ^v) ^k ^v -> (Map ^k ^v))]
  [map-remove-raw System.Collections.Immutable.ImmutableDictionary.Remove
    :instance : ((Map ^k ^v) ^k -> (Map ^k ^v))]
  [map-contains-raw System.Collections.Immutable.ImmutableDictionary.ContainsKey
    :instance : ((Map ^k ^v) ^k -> Bool)]
  ;; dict-keys/dict-values return IEnumerable at CLR level but are annotated as
  ;; TreeList to satisfy ZScheme's type system. Only safe when passed to create-treelist-from.
  [dict-keys System.Collections.Immutable.ImmutableDictionary.Keys
    :instance-property : ((Map ^k ^v) -> (TreeList ^k))]
  [dict-values System.Collections.Immutable.ImmutableDictionary.Values
    :instance-property : ((Map ^k ^v) -> (TreeList ^v))]
  [create-treelist-from System.Collections.Immutable.ImmutableList/CreateRange ^a
    : ((TreeList ^a) -> (TreeList ^a))]
  [map-from-mutable-raw System.Collections.Immutable.ImmutableDictionary/CreateRange ^k ^v
    : ((Mutable-Map ^k ^v) -> (Map ^k ^v))])

;; Constructors

(define (pair [k : ^k] [v : ^v]) : (Pair ^k ^v)
  :where (^k notnull)
  (pair-create k v))

(define (map-of [entries : (Pair ^k ^v) ...]) : (Map ^k ^v)
  :where (^k notnull)
  (map-create-range entries))

;; Exported functions

(define (length [m : (Map ^k ^v)]) : Int
  :where (^k notnull)
  (map-count-raw m))

(define (put [m : (Map ^k ^v)] [key : ^k] [val : ^v]) : (Map ^k ^v)
  :where (^k notnull)
  (map-set-raw m key val))

(define (remove [m : (Map ^k ^v)] [key : ^k]) : (Map ^k ^v)
  :where (^k notnull)
  (map-remove-raw m key))

(define (contains-key? [m : (Map ^k ^v)] [key : ^k]) : Bool
  :where (^k notnull)
  (map-contains-raw m key))

(define (empty? [m : (Map ^k ^v)]) : Bool
  :where (^k notnull)
  (= (map-count-raw m) 0))

(define (get [m : (Map ^k ^v)] [key : ^k]) : (Option ^v)
  :where (^k notnull)
  (if (map-contains-raw m key)
    (Some (map-item-raw m key))
    None))

(define (keys [m : (Map ^k ^v)]) : (TreeList ^k)
  :where (^k notnull)
  (create-treelist-from (dict-keys m)))

(define (values [m : (Map ^k ^v)]) : (TreeList ^v)
  :where (^k notnull)
  (create-treelist-from (dict-values m)))

;; Conversions

;; Mutable-Map -> Map via ImmutableDictionary.CreateRange<K,V>(IEnumerable<KeyValuePair<K,V>>).
(define (mutable-map->map [m : (Mutable-Map ^k ^v)]) : (Map ^k ^v)
  :where (^k notnull)
  (map-from-mutable-raw m))

(export pair map-of length put remove contains-key? empty? get keys values
        mutable-map->map)
