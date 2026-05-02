;; json.zs — JSON helpers via System.Text.Json (string-based for now).
;;
;; Binds the non-generic Serialize(object?, Type) overload. For typed
;; serialization, callers can construct a Type via (typeof ...) and pass it.
(module json)

(import-clr
  System
  System.Text.Json

  [json/serialize-typed System.Text.Json.JsonSerializer/Serialize
    : (Object System.Type -> String)])

(export json/serialize-typed)
