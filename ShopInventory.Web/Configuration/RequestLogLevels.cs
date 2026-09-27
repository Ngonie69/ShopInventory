using Microsoft.AspNetCore.Http;
using Serilog.Events;

namespace ShopInventory.Web.Configuration;

/// <summary>
/// The level Serilog's request logging gives each request.
/// </summary>
/// <remarks>
/// A staff page load fetches about 80 stylesheets, scripts and fonts, and each was an Information line
/// in the daily file and again in the IIS stdout log, burying the page requests that matter. A
/// static-asset request answered below 400 (a 200, or a 304 revalidation) now logs at Verbose, below the
/// configured minimum. A 404 still logs at Information and a failure or 5xx at Error, as Serilog's default
/// does, so a missing or broken asset stays visible.
/// </remarks>
public static class RequestLogLevels
{
    private static readonly string[] StaticPrefixes = ["/_content/", "/_framework/", "/css/", "/js/", "/lib/", "/fonts/", "/images/", "/icons/"];

    private static readonly HashSet<string> StaticExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".css", ".js", ".mjs", ".map", ".woff", ".woff2", ".ttf", ".eot", ".otf",
        ".png", ".jpg", ".jpeg", ".gif", ".svg", ".ico", ".webp", ".avif", ".webmanifest"
    };

    public static LogEventLevel For(HttpContext context, double elapsedMilliseconds, Exception? exception)
    {
        if (exception is not null || context.Response.StatusCode > 499)
        {
            return LogEventLevel.Error;
        }

        return context.Response.StatusCode < 400 && IsStaticAsset(context.Request.Path)
            ? LogEventLevel.Verbose
            : LogEventLevel.Information;
    }

    public static bool IsStaticAsset(PathString path)
    {
        var value = path.Value;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var prefix in StaticPrefixes)
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return StaticExtensions.Contains(Path.GetExtension(value));
    }
}
