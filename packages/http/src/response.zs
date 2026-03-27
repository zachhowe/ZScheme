;; response.zs — HTTP response type
(module response)

(record HttpResponse
  [status : Int]
  [reason : String]
  [body : String]
  [success : Bool])

(export HttpResponse)
