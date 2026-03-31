;; map.zs — Map operations via ImmutableDictionary<K,V>
(module map)

(import stdlib/option)

;; CLR bindings (internal)
(import-clr
  System.Collections.Immutable
  System.Collections.Generic
  [pair-create System.Collections.Generic.KeyValuePair/Create ^k ^v
    : (Fn [^k ^v] (Pair ^k ^v))]
  [map-create-range System.Collections.Immutable.ImmutableDictionary/CreateRange ^k ^v
    : (Fn [(Mutable-Array (Pair ^k ^v))] (Map ^k ^v))]
  [map-count-raw System.Collections.Immutable.ImmutableDictionary.Count
    :instance-property : (Fn [(Map ^k ^v)] Int)]
  [map-item-raw System.Collections.Immutable.ImmutableDictionary.Item
    :instance-indexer : (Fn [(Map ^k ^v) ^k] ^v)]
  [map-set-raw System.Collections.Immutable.ImmutableDictionary.SetItem
    :instance : (Fn [(Map ^k ^v) ^k ^v] (Map ^k ^v))]
  [map-remove-raw System.Collections.Immutable.ImmutableDictionary.Remove
    :instance : (Fn [(Map ^k ^v) ^k] (Map ^k ^v))]
  [map-contains-raw System.Collections.Immutable.ImmutableDictionary.ContainsKey
    :instance : (Fn [(Map ^k ^v) ^k] Bool)]
  ;; dict-keys/dict-values return IEnumerable at CLR level but are annotated as
  ;; List to satisfy ZScheme's type system. Only safe when passed to create-list-from.
  [dict-keys System.Collections.Immutable.ImmutableDictionary.Keys
    :instance-property : (Fn [(Map ^k ^v)] (List ^k))]
  [dict-values System.Collections.Immutable.ImmutableDictionary.Values
    :instance-property : (Fn [(Map ^k ^v)] (List ^v))]
  [create-list-from System.Collections.Immutable.ImmutableList/CreateRange ^a
    : (Fn [(List ^a)] (List ^a))])

;; Constructors

(define (pair [k : ^k] [v : ^v]) : (Pair ^k ^v)
  :where (^k notnull)
  (pair-create k v))

(define (map-of [entries : (Pair ^k ^v) ...]) : (Map ^k ^v)
  :where (^k notnull)
  (map-create-range entries))

;; Exported functions

(define (map/count [m : (Map ^k ^v)]) : Int
  :where (^k notnull)
  (map-count-raw m))

(define (map/put [m : (Map ^k ^v)] [key : ^k] [val : ^v]) : (Map ^k ^v)
  :where (^k notnull)
  (map-set-raw m key val))

(define (map/remove [m : (Map ^k ^v)] [key : ^k]) : (Map ^k ^v)
  :where (^k notnull)
  (map-remove-raw m key))

(define (map/contains-key? [m : (Map ^k ^v)] [key : ^k]) : Bool
  :where (^k notnull)
  (map-contains-raw m key))

(define (map/empty? [m : (Map ^k ^v)]) : Bool
  :where (^k notnull)
  (= (map-count-raw m) 0))

(define (map/get [m : (Map ^k ^v)] [key : ^k]) : (Option ^v)
  :where (^k notnull)
  (if (map-contains-raw m key)
    (Some (map-item-raw m key))
    None))

(define (map/keys [m : (Map ^k ^v)]) : (List ^k)
  :where (^k notnull)
  (create-list-from (dict-keys m)))

(define (map/values [m : (Map ^k ^v)]) : (List ^v)
  :where (^k notnull)
  (create-list-from (dict-values m)))

(export pair map-of map/count map/put map/remove map/contains-key? map/empty?
        map/get map/keys map/values)
