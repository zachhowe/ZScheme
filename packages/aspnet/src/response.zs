;; response.zs — HttpContext response writers
(module response)

(import-clr
  Microsoft.AspNetCore.Http
  ZScheme.AspNet.Bridge

  [response/status-set ZScheme.AspNet.Bridge.ResponseBridge/SetStatus
    : (Microsoft.AspNetCore.Http.HttpContext Int -> Unit)]

  [response/header-set ZScheme.AspNet.Bridge.ResponseBridge/SetHeader
    : (Microsoft.AspNetCore.Http.HttpContext String String -> Unit)]

  [response/write-string ZScheme.AspNet.Bridge.ResponseBridge/WriteString
    : (Microsoft.AspNetCore.Http.HttpContext String -> Task)]

  [response/write-json ZScheme.AspNet.Bridge.ResponseBridge/WriteJson
    : (Microsoft.AspNetCore.Http.HttpContext String -> Task)])

(export response/status-set response/header-set
        response/write-string response/write-json)
