using Microsoft.AspNetCore.Mvc;
using ShopInventory.Web.Common.ProblemDetails;

namespace ShopInventory.Web.Middleware;

/// <summary>
/// Proxies public API and Swagger requests to the backend API.
/// Existing mobile apps call the Web origin at /api/*, so these requests must
/// continue to reach the API even when IIS exposes only the Web site publicly.
/// Handles two path patterns:
///   /api/*           → backend API passthrough used by mobile clients
///   /swagger-proxy/* → explicit proxy route (used by the iframe src)
///   /swagger/*       → catches absolute-path resources loaded by Swagger UI inside the iframe
/// The exact path /swagger (no trailing content) is NOT intercepted — that's the Blazor page.
/// </summary>
public class SwaggerProxyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Uri _apiBaseUri;

    public SwaggerProxyMiddleware(
        RequestDelegate next,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory)
    {
        _next = next;
        _httpClientFactory = httpClientFactory;
        _apiBaseUri = new Uri((configuration["ApiSettings:BaseUrl"] ?? "http://localhost:5106").TrimEnd('/') + "/");
    }

    public async Task InvokeAsync(HttpContext context)
    {
        string? targetPath = null;

        if (context.Request.Path.StartsWithSegments("/api", out var apiRemaining))
        {
            targetPath = "/api" + apiRemaining.Value;
        }
        else if (context.Request.Path.StartsWithSegments("/swagger-proxy", out var proxyRemaining))
        {
            // Explicit proxy path: /swagger-proxy/swagger/index.html → /swagger/index.html
            targetPath = proxyRemaining.HasValue ? proxyRemaining.Value : "/swagger/index.html";
            if (string.IsNullOrEmpty(targetPath) || targetPath == "/")
                targetPath = "/swagger/index.html";
            if (!targetPath.StartsWith("/swagger"))
                targetPath = "/swagger" + targetPath;
        }
        else if (context.Request.Path.StartsWithSegments("/swagger", out var swaggerRemaining)
                 && swaggerRemaining.HasValue && !string.IsNullOrEmpty(swaggerRemaining.Value))
        {
            // Sub-path under /swagger/ (e.g. /swagger/v1/swagger.json, /swagger/index.html)
            // These are Swagger UI resources requested with absolute paths from inside the iframe
            targetPath = "/swagger" + swaggerRemaining.Value;
        }
        if (targetPath == null)
        {
            await _next(context);
            return;
        }

        var targetUri = new Uri(_apiBaseUri, targetPath.TrimStart('/') + context.Request.QueryString);

        var httpClient = _httpClientFactory.CreateClient("ShopInventoryApiUser");
        try
        {
            using var requestMessage = CreateProxyRequest(context, targetUri);
            using var response = await httpClient.SendAsync(
                requestMessage,
                HttpCompletionOption.ResponseHeadersRead,
                context.RequestAborted);

            context.Response.StatusCode = (int)response.StatusCode;

            CopyResponseHeaders(response, context.Response);

            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !context.RequestAborted.IsCancellationRequested)
        {
            var problemDetailsService = context.RequestServices.GetRequiredService<IProblemDetailsService>();
            var problemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status502BadGateway,
                Title = "Unable to reach API server.",
                Type = ProblemDetailsDefaults.GetType(StatusCodes.Status502BadGateway),
                Detail = "The Web application could not reach the ShopInventory API server."
            };

            await ProblemDetailsDefaults.WriteAsync(
                problemDetailsService,
                context,
                ex,
                problemDetails,
                context.RequestAborted);
        }
    }

    private static HttpRequestMessage CreateProxyRequest(HttpContext context, Uri targetUri)
    {
        var requestMessage = new HttpRequestMessage
        {
            Method = new HttpMethod(context.Request.Method),
            RequestUri = targetUri
        };

        foreach (var header in context.Request.Headers)
        {
            if (string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase))
                continue;

            if (IsForwardedHeader(header.Key))
                continue;

            if (!requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
            {
                requestMessage.Content ??= new StreamContent(context.Request.Body);
                requestMessage.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }

        AddForwardedHeaders(context, requestMessage);

        if (context.Request.ContentLength > 0 && requestMessage.Content is null)
        {
            requestMessage.Content = new StreamContent(context.Request.Body);
        }

        return requestMessage;
    }

    private static void AddForwardedHeaders(HttpContext context, HttpRequestMessage requestMessage)
    {
        var clientIp = context.Connection.RemoteIpAddress?.ToString();
        if (!string.IsNullOrWhiteSpace(clientIp))
        {
            requestMessage.Headers.TryAddWithoutValidation("X-Forwarded-For", clientIp);
        }

        requestMessage.Headers.TryAddWithoutValidation("X-Forwarded-Proto", context.Request.Scheme);

        var host = context.Request.Host.Value;
        if (!string.IsNullOrWhiteSpace(host))
        {
            requestMessage.Headers.TryAddWithoutValidation("X-Forwarded-Host", host);
        }
    }

    private static bool IsForwardedHeader(string headerName)
        => string.Equals(headerName, "X-Forwarded-For", StringComparison.OrdinalIgnoreCase)
           || string.Equals(headerName, "X-Forwarded-Proto", StringComparison.OrdinalIgnoreCase)
           || string.Equals(headerName, "X-Forwarded-Host", StringComparison.OrdinalIgnoreCase);

    private static void CopyResponseHeaders(HttpResponseMessage responseMessage, HttpResponse response)
    {
        foreach (var header in responseMessage.Headers)
        {
            response.Headers[header.Key] = header.Value.ToArray();
        }

        foreach (var header in responseMessage.Content.Headers)
        {
            response.Headers[header.Key] = header.Value.ToArray();
        }

        response.Headers.Remove("transfer-encoding");
    }

}

public static class SwaggerProxyMiddlewareExtensions
{
    public static IApplicationBuilder UseSwaggerProxy(this IApplicationBuilder app)
    {
        return app.UseMiddleware<SwaggerProxyMiddleware>();
    }
}
