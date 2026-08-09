;; response.zs — HttpContext response writers
(module response)

(import-clr
  Microsoft.AspNetCore.Http
  Microsoft.Extensions.Primitives

  ;; HttpContext.Response : HttpResponse
  [http-response HttpContext.Response
    :instance-property : (HttpContext -> HttpResponse)]

  ;; HttpResponse.StatusCode setter
  [status-set! HttpResponse.StatusCode
    :instance-property-set : (HttpResponse Int -> Unit)]

  ;; HttpResponse.ContentType setter (a plain string, so JSON avoids StringValues)
  [content-type-set! HttpResponse.ContentType
    :instance-property-set : (HttpResponse String -> Unit)]

  ;; HttpResponse.Headers : IHeaderDictionary
  [response-headers HttpResponse.Headers
    :instance-property : (HttpResponse -> IHeaderDictionary)]

  ;; HeaderDictionaryExtensions.Append(IHeaderDictionary, string, StringValues)
  [headers-append HeaderDictionaryExtensions/Append
    : (IHeaderDictionary String StringValues -> Unit)]

  ;; HttpResponseWritingExtensions.WriteAsync(HttpResponse, string); the trailing
  ;; CancellationToken/Encoding parameters are optional and supplied by the backend.
  [write-async HttpResponseWritingExtensions/WriteAsync
    : (HttpResponse String -> Task)])

(define (response/status-set [ctx : HttpContext] [code : Int]) : Unit
  (status-set! (http-response ctx) code))

(define (response/header-set [ctx : HttpContext]
                             [name : String] [value : String]) : Unit
  (headers-append (response-headers (http-response ctx)) name
                  (new StringValues value)))

(define (response/write-string [ctx : HttpContext] [body : String]) : Task
  (write-async (http-response ctx) body))

(define (response/write-json [ctx : HttpContext] [json : String]) : Task
  (begin
    (content-type-set! (http-response ctx) "application/json; charset=utf-8")
    (write-async (http-response ctx) json)))

(export response/status-set response/header-set
        response/write-string response/write-json)
