;; json-tests.zs — Tests for JSON serialize / deserialize via System.Text.Json
(namespace ZScheme.StdLib.Tests)
(module json-tests)

(import zunit)
(import stdlib/json)
(import stdlib/string)

;; A small record exercised by the generic serialize<T> / deserialize<T> bindings:
;; the concrete instantiation is resolved from the value's type (serialize) or the
;; expected result type (deserialize).
(define-record Widget [name : String] [count : Int])

(test-suite JsonTests
  (test-case serialize_emits_fields
    (let [s (json/serialize (Widget "gadget" 7))]
      (begin
        (check-true (contains? s "gadget"))
        (check-true (contains? s "7")))))

  (test-case serialize_int
    (check-equal? "42" (json/serialize 42)))

  (test-case roundtrip_preserves_fields
    (let [w (json/deserialize (json/serialize (Widget "gadget" 7)))]
      (begin
        (check-equal? "gadget" (Widget/name w))
        (check-equal? 7 (Widget/count w))))))
