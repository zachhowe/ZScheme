;; mutable-list.zs — Mutable-List operations via List<T>
(module mutable-list)

;; Map the ZScheme name `Mutable-List` to System.Collections.Generic.List<T> at codegen.
(define-type-alias (Mutable-List ^a) System.Collections.Generic.List)

;; CLR bindings (internal)
(import-clr
  System.Collections.Generic
  System.Linq
  [ml-count-raw System.Collections.Generic.List.Count
    :instance-property : ((Mutable-List ^a) -> Int)]
  [ml-item-raw System.Collections.Generic.List.Item
    :instance-indexer : ((Mutable-List ^a) Int -> ^a)]
  [ml-set-item-raw System.Collections.Generic.List.Item
    :instance-indexer-set : ((Mutable-List ^a) Int ^a -> Unit)]
  [ml-add-raw System.Collections.Generic.List.Add
    :instance : ((Mutable-List ^a) ^a -> Unit)]
  [ml-insert-raw System.Collections.Generic.List.Insert
    :instance : ((Mutable-List ^a) Int ^a -> Unit)]
  [ml-remove-at-raw System.Collections.Generic.List.RemoveAt
    :instance : ((Mutable-List ^a) Int -> Unit)]
  [ml-clear-raw System.Collections.Generic.List.Clear
    :instance : ((Mutable-List ^a) -> Unit)]
  [ml-contains-raw System.Collections.Generic.List.Contains
    :instance : ((Mutable-List ^a) ^a -> Bool)]
  [list-to-mutable-raw System.Linq.Enumerable/ToList ^a
    : ((List ^a) -> (Mutable-List ^a))])

;; Exported functions

(define (mutable-list/count [xs : (Mutable-List ^a)]) : Int
  (ml-count-raw xs))

(define (mutable-list/nth [xs : (Mutable-List ^a)] [i : Int]) : ^a
  (ml-item-raw xs i))

(define (mutable-list/set! [xs : (Mutable-List ^a)] [i : Int] [val : ^a]) : Unit
  (ml-set-item-raw xs i val))

(define (mutable-list/add! [xs : (Mutable-List ^a)] [val : ^a]) : Unit
  (ml-add-raw xs val))

(define (mutable-list/insert! [xs : (Mutable-List ^a)] [i : Int] [val : ^a]) : Unit
  (ml-insert-raw xs i val))

(define (mutable-list/remove-at! [xs : (Mutable-List ^a)] [i : Int]) : Unit
  (ml-remove-at-raw xs i))

(define (mutable-list/clear! [xs : (Mutable-List ^a)]) : Unit
  (ml-clear-raw xs))

(define (mutable-list/contains? [xs : (Mutable-List ^a)] [val : ^a]) : Bool
  (ml-contains-raw xs val))

(define (mutable-list/empty? [xs : (Mutable-List ^a)]) : Bool
  (= (ml-count-raw xs) 0))

;; Conversions

;; List -> Mutable-List via Enumerable.ToList<T>.
(define (list->mutable-list [xs : (List ^a)]) : (Mutable-List ^a)
  (list-to-mutable-raw xs))

(export mutable-list/count mutable-list/nth mutable-list/set!
        mutable-list/add! mutable-list/insert! mutable-list/remove-at!
        mutable-list/clear! mutable-list/contains? mutable-list/empty?
        list->mutable-list)
