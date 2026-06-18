;; response.zs — HttpContext response writers
(module response)

(import-clr
  Microsoft.AspNetCore.Http
  Microsoft.Extensions.Primitives

  ;; HttpContext.Response : HttpResponse
  [http-response Microsoft.AspNetCore.Http.HttpContext.Response
    :instance-property : (Microsoft.AspNetCore.Http.HttpContext
                          -> Microsoft.AspNetCore.Http.HttpResponse)]

  ;; HttpResponse.StatusCode setter
  [status-set! Microsoft.AspNetCore.Http.HttpResponse.StatusCode
    :instance-property-set : (Microsoft.AspNetCore.Http.HttpResponse Int -> Unit)]

  ;; HttpResponse.ContentType setter (a plain string, so JSON avoids StringValues)
  [content-type-set! Microsoft.AspNetCore.Http.HttpResponse.ContentType
    :instance-property-set : (Microsoft.AspNetCore.Http.HttpResponse String -> Unit)]

  ;; HttpResponse.Headers : IHeaderDictionary
  [response-headers Microsoft.AspNetCore.Http.HttpResponse.Headers
    :instance-property : (Microsoft.AspNetCore.Http.HttpResponse
                          -> Microsoft.AspNetCore.Http.IHeaderDictionary)]

  ;; HeaderDictionaryExtensions.Append(IHeaderDictionary, string, StringValues)
  [headers-append Microsoft.AspNetCore.Http.HeaderDictionaryExtensions/Append
    : (Microsoft.AspNetCore.Http.IHeaderDictionary
       String
       Microsoft.Extensions.Primitives.StringValues -> Unit)]

  ;; HttpResponseWritingExtensions.WriteAsync(HttpResponse, string); the trailing
  ;; CancellationToken/Encoding parameters are optional and supplied by the backend.
  [write-async Microsoft.AspNetCore.Http.HttpResponseWritingExtensions/WriteAsync
    : (Microsoft.AspNetCore.Http.HttpResponse String -> Task)])

(define (response/status-set [ctx : Microsoft.AspNetCore.Http.HttpContext] [code : Int]) : Unit
  (status-set! (http-response ctx) code))

(define (response/header-set [ctx : Microsoft.AspNetCore.Http.HttpContext]
                             [name : String] [value : String]) : Unit
  (headers-append (response-headers (http-response ctx)) name
                  (new Microsoft.Extensions.Primitives.StringValues value)))

(define (response/write-string [ctx : Microsoft.AspNetCore.Http.HttpContext] [body : String]) : Task
  (write-async (http-response ctx) body))

(define (response/write-json [ctx : Microsoft.AspNetCore.Http.HttpContext] [json : String]) : Task
  (begin
    (content-type-set! (http-response ctx) "application/json; charset=utf-8")
    (write-async (http-response ctx) json)))

(export response/status-set response/header-set
        response/write-string response/write-json)
