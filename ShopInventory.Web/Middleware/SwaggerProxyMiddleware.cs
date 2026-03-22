namespace ShopInventory.Web.Middleware;

/// <summary>
/// Proxies swagger requests to the backend API's Swagger UI,
/// so the browser can load Swagger from the same origin without mixed-content issues.
/// Handles two path patterns:
///   /swagger-proxy/* → explicit proxy route (used by the iframe src)
///   /swagger/*       → catches absolute-path resources loaded by Swagger UI inside the iframe
/// The exact path /swagger (no trailing content) is NOT intercepted — that's the Blazor page.
/// </summary>
public class SwaggerProxyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _apiBaseUrl;

    public SwaggerProxyMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        _next = next;
        _apiBaseUrl = (configuration["ApiSettings:BaseUrl"] ?? "http://localhost:5106").TrimEnd('/');
    }

    public async Task InvokeAsync(HttpContext context)
    {
        string? targetPath = null;

        if (context.Request.Path.StartsWithSegments("/swagger-proxy", out var proxyRemaining))
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
        else if (context.Request.Path.StartsWithSegments("/api", out _))
        {
            // Proxy API requests to the backend API server
            // This allows Swagger UI "Try It Out" to work when served through the web app
            await ProxyApiRequestAsync(context);
            return;
        }

        if (targetPath == null)
        {
            await _next(context);
            return;
        }

        var targetUrl = $"{_apiBaseUrl}{targetPath}{context.Request.QueryString}";

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        try
        {
            var response = await httpClient.GetAsync(targetUrl);

            context.Response.StatusCode = (int)response.StatusCode;

            if (response.Content.Headers.ContentType != null)
            {
                context.Response.ContentType = response.Content.Headers.ContentType.ToString();
            }

            await response.Content.CopyToAsync(context.Response.Body);
        }
        catch (Exception)
        {
            context.Response.StatusCode = 502;
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync("Unable to reach API server for Swagger documentation.");
        }
    }

    private async Task ProxyApiRequestAsync(HttpContext context)
    {
        var targetUrl = $"{_apiBaseUrl}{context.Request.Path}{context.Request.QueryString}";

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        try
        {
            var requestMessage = new HttpRequestMessage(new HttpMethod(context.Request.Method), targetUrl);

            // Forward request headers
            foreach (var header in context.Request.Headers)
            {
                if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase))
                    continue;
                requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }

            // Forward request body for POST/PUT/PATCH
            if (context.Request.ContentLength > 0 || context.Request.ContentType != null)
            {
                requestMessage.Content = new StreamContent(context.Request.Body);
                if (context.Request.ContentType != null)
                {
                    requestMessage.Content.Headers.ContentType =
                        System.Net.Http.Headers.MediaTypeHeaderValue.Parse(context.Request.ContentType);
                }
            }

            var response = await httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead);

            context.Response.StatusCode = (int)response.StatusCode;

            // Forward response headers
            foreach (var header in response.Headers)
            {
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }
            foreach (var header in response.Content.Headers)
            {
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }
            // Remove transfer-encoding since Kestrel handles this
            context.Response.Headers.Remove("transfer-encoding");

            await response.Content.CopyToAsync(context.Response.Body);
        }
        catch (Exception)
        {
            context.Response.StatusCode = 502;
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync("Unable to reach API server.");
        }
    }
}

public static class SwaggerProxyMiddlewareExtensions
{
    public static IApplicationBuilder UseSwaggerProxy(this IApplicationBuilder app)
    {
        return app.UseMiddleware<SwaggerProxyMiddleware>();
    }
}
