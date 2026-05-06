;; concurrent-dictionary.zs — Concurrent-Dictionary operations via ConcurrentDictionary<K,V>
(module concurrent-dictionary)

(import stdlib/option)

;; Map the ZScheme name `Concurrent-Dictionary` to System.Collections.Concurrent.ConcurrentDictionary<K,V> at codegen.
(define-type-alias (Concurrent-Dictionary ^k ^v)
  System.Collections.Concurrent.ConcurrentDictionary :from "System.Collections.Concurrent")

;; CLR bindings (internal)
(import-clr
  System.Collections.Concurrent
  System.Collections.Immutable
  [cd-count-raw System.Collections.Concurrent.ConcurrentDictionary.Count
    :instance-property : ((Concurrent-Dictionary ^k ^v) -> Int)]
  [cd-is-empty-raw System.Collections.Concurrent.ConcurrentDictionary.IsEmpty
    :instance-property : ((Concurrent-Dictionary ^k ^v) -> Bool)]
  [cd-item-raw System.Collections.Concurrent.ConcurrentDictionary.Item
    :instance-indexer : ((Concurrent-Dictionary ^k ^v) ^k -> ^v)]
  [cd-set-item-raw System.Collections.Concurrent.ConcurrentDictionary.Item
    :instance-indexer-set : ((Concurrent-Dictionary ^k ^v) ^k ^v -> Unit)]
  [cd-try-add-raw System.Collections.Concurrent.ConcurrentDictionary.TryAdd
    :instance : ((Concurrent-Dictionary ^k ^v) ^k ^v -> Bool)]
  [cd-try-get-raw System.Collections.Concurrent.ConcurrentDictionary.TryGetValue
    :instance : ((Concurrent-Dictionary ^k ^v) ^k -> (ValueTuple Bool ^v))]
  [cd-try-remove-raw System.Collections.Concurrent.ConcurrentDictionary.TryRemove
    :instance : ((Concurrent-Dictionary ^k ^v) ^k -> (ValueTuple Bool ^v))]
  [cd-contains-key-raw System.Collections.Concurrent.ConcurrentDictionary.ContainsKey
    :instance : ((Concurrent-Dictionary ^k ^v) ^k -> Bool)]
  [cd-clear-raw System.Collections.Concurrent.ConcurrentDictionary.Clear
    :instance : ((Concurrent-Dictionary ^k ^v) -> Unit)]
  [cd-keys-raw System.Collections.Concurrent.ConcurrentDictionary.Keys
    :instance-property : ((Concurrent-Dictionary ^k ^v) -> (List ^k))]
  [cd-values-raw System.Collections.Concurrent.ConcurrentDictionary.Values
    :instance-property : ((Concurrent-Dictionary ^k ^v) -> (List ^v))]
  [create-list-from System.Collections.Immutable.ImmutableList/CreateRange ^a
    : ((List ^a) -> (List ^a))])

;; Exported functions

;; Create an empty concurrent dictionary
(define (concurrent-dictionary/new) : (Concurrent-Dictionary ^k ^v)
  :where (^k notnull)
  (new (System.Collections.Concurrent.ConcurrentDictionary ^k ^v)))

(define (concurrent-dictionary/count [d : (Concurrent-Dictionary ^k ^v)]) : Int
  :where (^k notnull)
  (cd-count-raw d))

(define (concurrent-dictionary/empty? [d : (Concurrent-Dictionary ^k ^v)]) : Bool
  :where (^k notnull)
  (cd-is-empty-raw d))

;; Unconditionally set a key-value pair
(define (concurrent-dictionary/put! [d : (Concurrent-Dictionary ^k ^v)] [key : ^k] [val : ^v]) : Unit
  :where (^k notnull)
  (cd-set-item-raw d key val))

;; Try to add a key-value pair. Returns true if added, false if key already exists.
(define (concurrent-dictionary/try-add! [d : (Concurrent-Dictionary ^k ^v)] [key : ^k] [val : ^v]) : Bool
  :where (^k notnull)
  (cd-try-add-raw d key val))

;; Get a value by key, returning Option
(define (concurrent-dictionary/get [d : (Concurrent-Dictionary ^k ^v)] [key : ^k]) : (Option ^v)
  :where (^k notnull)
  (if (cd-contains-key-raw d key)
    (Some (cd-item-raw d key))
    None))

;; Try to get a value by key. Returns (Bool, ^v) tuple.
(define (concurrent-dictionary/try-get [d : (Concurrent-Dictionary ^k ^v)] [key : ^k]) : (ValueTuple Bool ^v)
  :where (^k notnull)
  (cd-try-get-raw d key))

;; Try to remove a key. Returns (Bool, ^v) tuple.
(define (concurrent-dictionary/try-remove! [d : (Concurrent-Dictionary ^k ^v)] [key : ^k]) : (ValueTuple Bool ^v)
  :where (^k notnull)
  (cd-try-remove-raw d key))

(define (concurrent-dictionary/contains-key? [d : (Concurrent-Dictionary ^k ^v)] [key : ^k]) : Bool
  :where (^k notnull)
  (cd-contains-key-raw d key))

(define (concurrent-dictionary/clear! [d : (Concurrent-Dictionary ^k ^v)]) : Unit
  :where (^k notnull)
  (cd-clear-raw d))

(define (concurrent-dictionary/keys [d : (Concurrent-Dictionary ^k ^v)]) : (List ^k)
  :where (^k notnull)
  (create-list-from (cd-keys-raw d)))

(define (concurrent-dictionary/values [d : (Concurrent-Dictionary ^k ^v)]) : (List ^v)
  :where (^k notnull)
  (create-list-from (cd-values-raw d)))

(export concurrent-dictionary/new concurrent-dictionary/count concurrent-dictionary/empty?
        concurrent-dictionary/put! concurrent-dictionary/try-add!
        concurrent-dictionary/get concurrent-dictionary/try-get
        concurrent-dictionary/try-remove! concurrent-dictionary/contains-key?
        concurrent-dictionary/clear! concurrent-dictionary/keys concurrent-dictionary/values)
