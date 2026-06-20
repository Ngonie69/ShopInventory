using Microsoft.AspNetCore.Diagnostics;

namespace ShopInventory.Web.Common.ProblemDetails;

public sealed class GlobalExceptionHandler(
    ILogger<GlobalExceptionHandler> logger,
    IHostEnvironment environment,
    IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    public ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (!ProblemDetailsDefaults.ShouldWriteProblemDetails(httpContext))
        {
            return ValueTask.FromResult(false);
        }

        logger.LogError(
            exception,
            "Unhandled exception for {Method} {Path}.",
            httpContext.Request.Method,
            httpContext.Request.Path);

        var problemDetails = new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "An unexpected error occurred.",
            Type = ProblemDetailsDefaults.GetType(StatusCodes.Status500InternalServerError),
            Detail = environment.IsDevelopment()
                ? exception.Message
                : "The request could not be completed. Use the traceId when reviewing server logs."
        };

        if (environment.IsDevelopment())
        {
            problemDetails.Extensions["exceptionType"] = exception.GetType().FullName;
        }

        return ProblemDetailsDefaults.WriteAsync(
            problemDetailsService,
            httpContext,
            exception,
            problemDetails,
            cancellationToken);
    }
}
