using Microsoft.AspNetCore.Diagnostics;

namespace ShopInventory.Common.ProblemDetails;

public sealed class RequestCanceledExceptionHandler(
    ILogger<RequestCanceledExceptionHandler> logger) : IExceptionHandler
{
    private const int ClientClosedRequestStatusCode = 499;

    public ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not OperationCanceledException || !httpContext.RequestAborted.IsCancellationRequested)
        {
            return ValueTask.FromResult(false);
        }

        logger.LogInformation(
            "Request cancelled by client for {Method} {Path}.",
            httpContext.Request.Method,
            httpContext.Request.Path);

        if (!httpContext.Response.HasStarted)
        {
            httpContext.Response.StatusCode = ClientClosedRequestStatusCode;
        }

        return ValueTask.FromResult(true);
    }
}
