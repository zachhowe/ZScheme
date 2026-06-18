;; request.zs — HttpContext request accessors
(module request)

(import-clr
  Microsoft.AspNetCore.Http
  ZScheme.AspNet.Bridge

  [request/method ZScheme.AspNet.Bridge.RequestBridge/GetMethod
    : (Microsoft.AspNetCore.Http.HttpContext -> String)]

  [request/path ZScheme.AspNet.Bridge.RequestBridge/GetPath
    : (Microsoft.AspNetCore.Http.HttpContext -> String)]

  ;; Returns the route value for `key`, or `fallback` when absent.
  [request/route-value ZScheme.AspNet.Bridge.RequestBridge/GetRouteValue
    : (Microsoft.AspNetCore.Http.HttpContext String String -> String)]

  ;; Returns the query string value for `key`, or `fallback` when absent.
  [request/query ZScheme.AspNet.Bridge.RequestBridge/GetQuery
    : (Microsoft.AspNetCore.Http.HttpContext String String -> String)]

  ;; Returns the request header value for `name`, or `fallback` when absent.
  [request/header ZScheme.AspNet.Bridge.RequestBridge/GetHeader
    : (Microsoft.AspNetCore.Http.HttpContext String String -> String)]

  [request/read-body-string ZScheme.AspNet.Bridge.RequestBridge/ReadBodyString
    : (Microsoft.AspNetCore.Http.HttpContext -> (Task String))])

(import stdlib/option)

(import-clr
  System
  ;; Int32.TryParse(string, out int) — out param surfaces as (ValueTuple Bool Int).
  [int-try-parse System.Int32/TryParse])

;; Parse a query-string value as an Int, returning None when absent or non-numeric.
(define (request/query-int [ctx : Microsoft.AspNetCore.Http.HttpContext] [key : String]) : (Option Int)
  (match (int-try-parse (request/query ctx key ""))
    [(values ok n) (if ok (Some n) None)]))

;; Parse a route value as an Int, returning None when absent or non-numeric.
(define (request/route-value-int [ctx : Microsoft.AspNetCore.Http.HttpContext] [key : String]) : (Option Int)
  (match (int-try-parse (request/route-value ctx key ""))
    [(values ok n) (if ok (Some n) None)]))

(export request/method request/path
        request/route-value request/query request/header
        request/read-body-string
        request/query-int request/route-value-int
        Option Some None)
