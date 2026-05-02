using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace ZScheme.AspNet.Bridge;

/// <summary>
///     Wrappers around ASP.NET Core APIs whose signatures are ergonomic for
///     ZScheme's <c>import-clr</c> binder. Each method has unambiguous overloads
///     and accepts <see cref="Func{HttpContext, Task}"/> directly so ZScheme
///     handler functions can be passed without delegate-type conversions.
/// </summary>
public static class WebAppBridge
{
    public static WebApplicationBuilder CreateBuilder() =>
        WebApplication.CreateBuilder();

    public static WebApplication BuildApp(WebApplicationBuilder builder) =>
        builder.Build();

    public static void Run(WebApplication app) => app.Run();

    public static Task RunAsync(WebApplication app) => app.RunAsync();

    public static void AddUrl(WebApplication app, string url) =>
        app.Urls.Add(url);

    public static string GetFirstUrl(WebApplication app)
    {
        foreach (var url in app.Urls) return url;
        return string.Empty;
    }
}

public static class RouterBridge
{
    public static void MapGet(WebApplication app, string pattern, Func<HttpContext, Task> handler) =>
        app.MapGet(pattern, handler);

    public static void MapPost(WebApplication app, string pattern, Func<HttpContext, Task> handler) =>
        app.MapPost(pattern, handler);

    public static void MapPut(WebApplication app, string pattern, Func<HttpContext, Task> handler) =>
        app.MapPut(pattern, handler);

    public static void MapPatch(WebApplication app, string pattern, Func<HttpContext, Task> handler) =>
        app.MapPatch(pattern, handler);

    public static void MapDelete(WebApplication app, string pattern, Func<HttpContext, Task> handler) =>
        app.MapDelete(pattern, handler);
}

public static class MiddlewareBridge
{
    public static void Use(WebApplication app, Func<HttpContext, Func<Task>, Task> middleware) =>
        app.Use(middleware);
}

public static class RequestBridge
{
    public static string GetMethod(HttpContext ctx) => ctx.Request.Method;

    public static string GetPath(HttpContext ctx) => ctx.Request.Path.Value ?? "";

    public static string GetRouteValue(HttpContext ctx, string key, string fallback) =>
        ctx.Request.RouteValues.TryGetValue(key, out var v) ? v?.ToString() ?? fallback : fallback;

    public static string GetQuery(HttpContext ctx, string key, string fallback) =>
        ctx.Request.Query.TryGetValue(key, out var v) ? v.ToString() : fallback;

    public static string GetHeader(HttpContext ctx, string key, string fallback) =>
        ctx.Request.Headers.TryGetValue(key, out var v) ? v.ToString() : fallback;

    public static async Task<string> ReadBodyString(HttpContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.Body);
        return await reader.ReadToEndAsync();
    }
}

public static class ResponseBridge
{
    public static void SetStatus(HttpContext ctx, int code) => ctx.Response.StatusCode = code;

    public static void SetHeader(HttpContext ctx, string name, string value) =>
        ctx.Response.Headers.Append(name, value);

    public static Task WriteString(HttpContext ctx, string body) =>
        ctx.Response.WriteAsync(body);

    public static Task WriteJson(HttpContext ctx, string json)
    {
        ctx.Response.Headers.ContentType = "application/json; charset=utf-8";
        return ctx.Response.WriteAsync(json);
    }
}
