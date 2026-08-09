;; json.zs — JSON helpers via System.Text.Json (string-based for now).
;;
;; `json/serialize` / `json/deserialize` bind the generic Serialize<T> / Deserialize<T>
;; overloads; the compiler resolves the concrete instantiation at the call site from the
;; value's type (serialize) or the expected result type (deserialize).
;;
;; `json/serialize-typed` binds the non-generic Serialize(object?, Type) overload for
;; callers that already hold a `System.Type` (e.g. via (typeof MyRecord)).
(module json)

(import-clr
  System
  System.Text.Json

  [json/serialize JsonSerializer/Serialize ^a
    : (^a -> String)]

  [json/deserialize JsonSerializer/Deserialize ^a
    : (String -> ^a)]

  [json/serialize-typed JsonSerializer/Serialize
    : (Object Type -> String)])

(export json/serialize json/deserialize json/serialize-typed)
