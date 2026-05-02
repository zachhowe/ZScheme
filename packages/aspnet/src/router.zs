;; router.zs — HTTP route registration (GET/POST/PUT/PATCH/DELETE)
(module router)

(import-clr
  Microsoft.AspNetCore.Builder
  Microsoft.AspNetCore.Http
  ZScheme.AspNet.Bridge

  [route/get ZScheme.AspNet.Bridge.RouterBridge/MapGet
    : (Microsoft.AspNetCore.Builder.WebApplication
       String
       (Microsoft.AspNetCore.Http.HttpContext -> Task)
       -> Unit)]

  [route/post ZScheme.AspNet.Bridge.RouterBridge/MapPost
    : (Microsoft.AspNetCore.Builder.WebApplication
       String
       (Microsoft.AspNetCore.Http.HttpContext -> Task)
       -> Unit)]

  [route/put ZScheme.AspNet.Bridge.RouterBridge/MapPut
    : (Microsoft.AspNetCore.Builder.WebApplication
       String
       (Microsoft.AspNetCore.Http.HttpContext -> Task)
       -> Unit)]

  [route/patch ZScheme.AspNet.Bridge.RouterBridge/MapPatch
    : (Microsoft.AspNetCore.Builder.WebApplication
       String
       (Microsoft.AspNetCore.Http.HttpContext -> Task)
       -> Unit)]

  [route/delete ZScheme.AspNet.Bridge.RouterBridge/MapDelete
    : (Microsoft.AspNetCore.Builder.WebApplication
       String
       (Microsoft.AspNetCore.Http.HttpContext -> Task)
       -> Unit)])

(export route/get route/post route/put route/patch route/delete)
