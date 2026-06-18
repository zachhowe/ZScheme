;; app.zs — WebApplication lifecycle (create-builder, build, run)
(module app)

(import-clr
  Microsoft.AspNetCore.Builder
  ZScheme.AspNet.Bridge

  [app/create-builder ZScheme.AspNet.Bridge.WebAppBridge/CreateBuilder
    : (-> Microsoft.AspNetCore.Builder.WebApplicationBuilder)]

  [app/build ZScheme.AspNet.Bridge.WebAppBridge/BuildApp
    : (Microsoft.AspNetCore.Builder.WebApplicationBuilder
       -> Microsoft.AspNetCore.Builder.WebApplication)]

  [app/run ZScheme.AspNet.Bridge.WebAppBridge/Run
    : (Microsoft.AspNetCore.Builder.WebApplication -> Unit)]

  [app/run-async ZScheme.AspNet.Bridge.WebAppBridge/RunAsync
    : (Microsoft.AspNetCore.Builder.WebApplication -> Task)]

  ;; Start Kestrel without blocking; the returned Task completes once the server
  ;; is bound and listening (app/first-url then holds the resolved port).
  [app/start ZScheme.AspNet.Bridge.WebAppBridge/StartServer
    : (Microsoft.AspNetCore.Builder.WebApplication -> Task)]

  ;; Gracefully stop and dispose the host (Kestrel, sockets, DI container).
  [app/shutdown ZScheme.AspNet.Bridge.WebAppBridge/Shutdown
    : (Microsoft.AspNetCore.Builder.WebApplication -> Unit)]

  [app/url-add ZScheme.AspNet.Bridge.WebAppBridge/AddUrl
    : (Microsoft.AspNetCore.Builder.WebApplication String -> Unit)]

  [app/first-url ZScheme.AspNet.Bridge.WebAppBridge/GetFirstUrl
    : (Microsoft.AspNetCore.Builder.WebApplication -> String)])

(export app/create-builder app/build app/run app/run-async
        app/start app/shutdown app/url-add app/first-url)
