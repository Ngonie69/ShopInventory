using Microsoft.AspNetCore.Diagnostics;

namespace ShopInventory.Web.Common.ProblemDetails;

public sealed class RequestInputExceptionHandler(
    ILogger<RequestInputExceptionHandler> logger,
    IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    public ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (!IsRequestInputException(exception)
            || !ProblemDetailsDefaults.ShouldWriteProblemDetails(httpContext))
        {
            return ValueTask.FromResult(false);
        }

        logger.LogInformation(
            exception,
            "Request input error for {Method} {Path}.",
            httpContext.Request.Method,
            httpContext.Request.Path);

        var problemDetails = new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "The request could not be processed.",
            Type = ProblemDetailsDefaults.GetType(StatusCodes.Status400BadRequest),
            Detail = exception.Message
        };

        return ProblemDetailsDefaults.WriteAsync(
            problemDetailsService,
            httpContext,
            exception,
            problemDetails,
            cancellationToken);
    }

    private static bool IsRequestInputException(Exception exception)
        => exception is BadHttpRequestException
            or InvalidDataException
            or ArgumentException and not ArgumentNullException;
}
