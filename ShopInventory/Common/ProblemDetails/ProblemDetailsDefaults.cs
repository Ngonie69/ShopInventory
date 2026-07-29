using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace ShopInventory.Common.ProblemDetails;

public static class ProblemDetailsDefaults
{
    public static void Customize(ProblemDetailsContext context)
    {
        Apply(context.HttpContext, context.ProblemDetails);
    }

    public static void Apply(HttpContext httpContext, Microsoft.AspNetCore.Mvc.ProblemDetails problemDetails)
    {
        problemDetails.Status ??= httpContext.Response.StatusCode;
        problemDetails.Type ??= GetType(problemDetails.Status);
        problemDetails.Title ??= GetTitle(problemDetails.Status);
        problemDetails.Instance ??= httpContext.Request.Path.Value;

        if (!problemDetails.Extensions.ContainsKey("traceId"))
        {
            problemDetails.Extensions["traceId"] = Activity.Current?.Id ?? httpContext.TraceIdentifier;
        }
    }

    public static async ValueTask<bool> WriteAsync(
        IProblemDetailsService problemDetailsService,
        HttpContext httpContext,
        Exception? exception,
        Microsoft.AspNetCore.Mvc.ProblemDetails problemDetails,
        CancellationToken cancellationToken)
    {
        if (httpContext.Response.HasStarted)
        {
            return false;
        }

        Apply(httpContext, problemDetails);
        httpContext.Response.StatusCode = problemDetails.Status ?? StatusCodes.Status500InternalServerError;

        var problemDetailsContext = new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = problemDetails
        };

        if (await problemDetailsService.TryWriteAsync(problemDetailsContext))
        {
            return true;
        }

        await httpContext.Response.WriteAsJsonAsync(problemDetails, cancellationToken);
        return true;
    }

    public static string? GetType(int? statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest => "https://tools.ietf.org/html/rfc9110#section-15.5.1",
        StatusCodes.Status401Unauthorized => "https://tools.ietf.org/html/rfc9110#section-15.5.2",
        StatusCodes.Status403Forbidden => "https://tools.ietf.org/html/rfc9110#section-15.5.4",
        StatusCodes.Status404NotFound => "https://tools.ietf.org/html/rfc9110#section-15.5.5",
        StatusCodes.Status409Conflict => "https://tools.ietf.org/html/rfc9110#section-15.5.10",
        StatusCodes.Status413PayloadTooLarge => "https://tools.ietf.org/html/rfc9110#section-15.5.14",
        StatusCodes.Status429TooManyRequests => "https://tools.ietf.org/html/rfc6585#section-4",
        StatusCodes.Status500InternalServerError => "https://tools.ietf.org/html/rfc9110#section-15.6.1",
        StatusCodes.Status502BadGateway => "https://tools.ietf.org/html/rfc9110#section-15.6.3",
        StatusCodes.Status503ServiceUnavailable => "https://tools.ietf.org/html/rfc9110#section-15.6.4",
        StatusCodes.Status504GatewayTimeout => "https://tools.ietf.org/html/rfc9110#section-15.6.5",
        _ => null
    };

    private static string? GetTitle(int? statusCode)
        => statusCode.HasValue ? ReasonPhrases.GetReasonPhrase(statusCode.Value) : null;
}
