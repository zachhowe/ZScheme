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

  [app/url-add ZScheme.AspNet.Bridge.WebAppBridge/AddUrl
    : (Microsoft.AspNetCore.Builder.WebApplication String -> Unit)]

  [app/first-url ZScheme.AspNet.Bridge.WebAppBridge/GetFirstUrl
    : (Microsoft.AspNetCore.Builder.WebApplication -> String)])

(export app/create-builder app/build app/run app/run-async app/url-add app/first-url)
