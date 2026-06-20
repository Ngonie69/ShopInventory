using Microsoft.AspNetCore.Diagnostics;

namespace ShopInventory.Web.Common.ProblemDetails;

public sealed class AuthorizationExceptionHandler(
    ILogger<AuthorizationExceptionHandler> logger,
    IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    public ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not UnauthorizedAccessException
            || !ProblemDetailsDefaults.ShouldWriteProblemDetails(httpContext))
        {
            return ValueTask.FromResult(false);
        }

        logger.LogWarning(
            exception,
            "Access denied for {Method} {Path}.",
            httpContext.Request.Method,
            httpContext.Request.Path);

        var problemDetails = new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = StatusCodes.Status403Forbidden,
            Title = "Access denied.",
            Type = ProblemDetailsDefaults.GetType(StatusCodes.Status403Forbidden),
            Detail = "You do not have permission to perform this action."
        };

        return ProblemDetailsDefaults.WriteAsync(
            problemDetailsService,
            httpContext,
            exception,
            problemDetails,
            cancellationToken);
    }
}
