;; mutable-map.zs — Mutable-Map operations via Dictionary<K,V>
(module mutable-map)

(import stdlib/option)

;; Map the ZScheme name `Mutable-Map` to System.Collections.Generic.Dictionary<K,V> at codegen.
(define-type-alias (Mutable-Map ^k ^v) System.Collections.Generic.Dictionary)

;; CLR bindings (internal)
(import-clr
  System.Collections.Generic
  System.Collections.Immutable
  [mm-count-raw System.Collections.Generic.Dictionary.Count
    :instance-property : ((Mutable-Map ^k ^v) -> Int)]
  [mm-item-raw System.Collections.Generic.Dictionary.Item
    :instance-indexer : ((Mutable-Map ^k ^v) ^k -> ^v)]
  [mm-set-item-raw System.Collections.Generic.Dictionary.Item
    :instance-indexer-set : ((Mutable-Map ^k ^v) ^k ^v -> Unit)]
  [mm-contains-key-raw System.Collections.Generic.Dictionary.ContainsKey
    :instance : ((Mutable-Map ^k ^v) ^k -> Bool)]
  [mm-remove-raw System.Collections.Generic.Dictionary.Remove
    :instance : ((Mutable-Map ^k ^v) ^k -> Bool)]
  [mm-clear-raw System.Collections.Generic.Dictionary.Clear
    :instance : ((Mutable-Map ^k ^v) -> Unit)]
  [mm-keys-raw System.Collections.Generic.Dictionary.Keys
    :instance-property : ((Mutable-Map ^k ^v) -> (List ^k))]
  [mm-values-raw System.Collections.Generic.Dictionary.Values
    :instance-property : ((Mutable-Map ^k ^v) -> (List ^v))]
  [create-list-from System.Collections.Immutable.ImmutableList/CreateRange ^a
    : ((List ^a) -> (List ^a))])

;; Exported functions

;; Create an empty mutable map
(define (mutable-map/new) : (Mutable-Map ^k ^v)
  :where (^k notnull)
  (new (System.Collections.Generic.Dictionary ^k ^v)))

(define (length [m : (Mutable-Map ^k ^v)]) : Int
  :where (^k notnull)
  (mm-count-raw m))

(define (put! [m : (Mutable-Map ^k ^v)] [key : ^k] [val : ^v]) : Unit
  :where (^k notnull)
  (mm-set-item-raw m key val))

(define (get [m : (Mutable-Map ^k ^v)] [key : ^k]) : (Option ^v)
  :where (^k notnull)
  (if (mm-contains-key-raw m key)
    (Some (mm-item-raw m key))
    None))

(define (remove! [m : (Mutable-Map ^k ^v)] [key : ^k]) : Bool
  :where (^k notnull)
  (mm-remove-raw m key))

(define (contains-key? [m : (Mutable-Map ^k ^v)] [key : ^k]) : Bool
  :where (^k notnull)
  (mm-contains-key-raw m key))

(define (clear! [m : (Mutable-Map ^k ^v)]) : Unit
  :where (^k notnull)
  (mm-clear-raw m))

(define (empty? [m : (Mutable-Map ^k ^v)]) : Bool
  :where (^k notnull)
  (= (mm-count-raw m) 0))

(define (keys [m : (Mutable-Map ^k ^v)]) : (List ^k)
  :where (^k notnull)
  (create-list-from (mm-keys-raw m)))

(define (values [m : (Mutable-Map ^k ^v)]) : (List ^v)
  :where (^k notnull)
  (create-list-from (mm-values-raw m)))

;; Conversions

;; Map -> Mutable-Map by constructing a new Dictionary from the immutable view.
(define (map->mutable-map [m : (Map ^k ^v)]) : (Mutable-Map ^k ^v)
  :where (^k notnull)
  (new (System.Collections.Generic.Dictionary ^k ^v) m))

(export mutable-map/new length put! get remove!
        contains-key? clear! empty? keys values
        map->mutable-map)
