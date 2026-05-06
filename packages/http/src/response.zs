;; response.zs — HTTP response type
(module response)

(define-record HttpResponse
  [status : Int]
  [reason : String]
  [body : String]
  [success : Bool])

(export HttpResponse)
