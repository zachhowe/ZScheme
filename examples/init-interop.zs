;; Consuming CLR properties via interop
;;
;; Use :instance-property and :instance-property-init to access
;; properties on CLR types. :instance-property-init marks an
;; init-only setter, which at the IL level behaves identically
;; to a regular property set.

(namespace ZScheme.Examples)

(module init-interop)

;; Read and write the Content property of HttpRequestMessage
(import-clr
  [get-content System.Net.Http.HttpRequestMessage.Content
    :instance-property : (System.Net.Http.HttpRequestMessage -> (Nullable System.Net.Http.HttpContent))]
  [set-content System.Net.Http.HttpRequestMessage.Content
    :instance-property-init : (System.Net.Http.HttpRequestMessage System.Net.Http.HttpContent -> Unit)])

(define (replace-content [msg : System.Net.Http.HttpRequestMessage]
                         [c : System.Net.Http.HttpContent]) : Unit
  (set-content msg c))

(define (read-content [msg : System.Net.Http.HttpRequestMessage]) : (Nullable System.Net.Http.HttpContent)
  (get-content msg))
