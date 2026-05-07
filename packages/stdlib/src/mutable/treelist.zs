;; mutable-treelist.zs — Mutable-TreeList operations via List<T>
(module mutable-treelist)

;; Map the ZScheme name `Mutable-TreeList` to System.Collections.Generic.List<T> at codegen.
(define-type-alias (Mutable-TreeList ^a) System.Collections.Generic.List)

;; CLR bindings (internal)
(import-clr
  System.Collections.Generic
  System.Linq
  [ml-count-raw System.Collections.Generic.List.Count
    :instance-property : ((Mutable-TreeList ^a) -> Int)]
  [ml-item-raw System.Collections.Generic.List.Item
    :instance-indexer : ((Mutable-TreeList ^a) Int -> ^a)]
  [ml-set-item-raw System.Collections.Generic.List.Item
    :instance-indexer-set : ((Mutable-TreeList ^a) Int ^a -> Unit)]
  [ml-add-raw System.Collections.Generic.List.Add
    :instance : ((Mutable-TreeList ^a) ^a -> Unit)]
  [ml-insert-raw System.Collections.Generic.List.Insert
    :instance : ((Mutable-TreeList ^a) Int ^a -> Unit)]
  [ml-remove-at-raw System.Collections.Generic.List.RemoveAt
    :instance : ((Mutable-TreeList ^a) Int -> Unit)]
  [ml-clear-raw System.Collections.Generic.List.Clear
    :instance : ((Mutable-TreeList ^a) -> Unit)]
  [ml-contains-raw System.Collections.Generic.List.Contains
    :instance : ((Mutable-TreeList ^a) ^a -> Bool)]
  [treelist-to-mutable-raw System.Linq.Enumerable/ToList ^a
    : ((TreeList ^a) -> (Mutable-TreeList ^a))])

;; Exported functions

(define (mutable-treelist-length [xs : (Mutable-TreeList ^a)]) : Int
  (ml-count-raw xs))

(define (mutable-treelist-ref [xs : (Mutable-TreeList ^a)] [i : Int]) : ^a
  (ml-item-raw xs i))

(define (mutable-treelist-set! [xs : (Mutable-TreeList ^a)] [i : Int] [val : ^a]) : Unit
  (ml-set-item-raw xs i val))

(define (mutable-treelist-add! [xs : (Mutable-TreeList ^a)] [val : ^a]) : Unit
  (ml-add-raw xs val))

(define (mutable-treelist-insert! [xs : (Mutable-TreeList ^a)] [i : Int] [val : ^a]) : Unit
  (ml-insert-raw xs i val))

(define (mutable-treelist-delete! [xs : (Mutable-TreeList ^a)] [i : Int]) : Unit
  (ml-remove-at-raw xs i))

(define (mutable-treelist-clear! [xs : (Mutable-TreeList ^a)]) : Unit
  (ml-clear-raw xs))

(define (mutable-treelist-member? [xs : (Mutable-TreeList ^a)] [val : ^a]) : Bool
  (ml-contains-raw xs val))

(define (mutable-treelist-empty? [xs : (Mutable-TreeList ^a)]) : Bool
  (= (ml-count-raw xs) 0))

;; Conversions

;; TreeList -> Mutable-TreeList via Enumerable.ToList<T>.
(define (treelist-copy [xs : (TreeList ^a)]) : (Mutable-TreeList ^a)
  (treelist-to-mutable-raw xs))

(export mutable-treelist-length mutable-treelist-ref mutable-treelist-set!
        mutable-treelist-add! mutable-treelist-insert! mutable-treelist-delete!
        mutable-treelist-clear! mutable-treelist-member? mutable-treelist-empty?
        treelist-copy)
