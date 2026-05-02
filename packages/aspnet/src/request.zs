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

(export request/method request/path
        request/route-value request/query request/header
        request/read-body-string)
