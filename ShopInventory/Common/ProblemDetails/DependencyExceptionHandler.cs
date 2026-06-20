using Microsoft.AspNetCore.Diagnostics;
using ShopInventory.Services;

namespace ShopInventory.Common.ProblemDetails;

public sealed class DependencyExceptionHandler(
    ILogger<DependencyExceptionHandler> logger,
    IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    public ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        return exception switch
        {
            TimeoutException => HandleTimeoutAsync(httpContext, exception, cancellationToken),
            TaskCanceledException when !httpContext.RequestAborted.IsCancellationRequested =>
                HandleTimeoutAsync(httpContext, exception, cancellationToken),
            OpenWAGatewayException openWaException =>
                HandleOpenWaGatewayAsync(httpContext, openWaException, cancellationToken),
            HttpRequestException httpRequestException =>
                HandleHttpDependencyAsync(httpContext, httpRequestException, cancellationToken),
            _ => ValueTask.FromResult(false)
        };
    }

    private ValueTask<bool> HandleTimeoutAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        logger.LogWarning(
            exception,
            "Dependency timeout for {Method} {Path}.",
            httpContext.Request.Method,
            httpContext.Request.Path);

        var problemDetails = new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = StatusCodes.Status504GatewayTimeout,
            Title = "A downstream service timed out.",
            Type = ProblemDetailsDefaults.GetType(StatusCodes.Status504GatewayTimeout),
            Detail = "The request could not be completed because a downstream service did not respond in time."
        };
        problemDetails.Extensions["retryable"] = true;

        return ProblemDetailsDefaults.WriteAsync(
            problemDetailsService,
            httpContext,
            exception,
            problemDetails,
            cancellationToken);
    }

    private ValueTask<bool> HandleOpenWaGatewayAsync(
        HttpContext httpContext,
        OpenWAGatewayException exception,
        CancellationToken cancellationToken)
    {
        logger.LogWarning(
            exception,
            "OpenWA gateway request failed for {Method} {Path} with upstream status {StatusCode}.",
            httpContext.Request.Method,
            httpContext.Request.Path,
            (int)exception.StatusCode);

        var problemDetails = new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = StatusCodes.Status502BadGateway,
            Title = "OpenWA gateway request failed.",
            Type = ProblemDetailsDefaults.GetType(StatusCodes.Status502BadGateway),
            Detail = exception.Message
        };
        problemDetails.Extensions["upstreamStatus"] = (int)exception.StatusCode;

        if (!string.IsNullOrWhiteSpace(exception.ReasonPhrase))
        {
            problemDetails.Extensions["upstreamReason"] = exception.ReasonPhrase;
        }

        return ProblemDetailsDefaults.WriteAsync(
            problemDetailsService,
            httpContext,
            exception,
            problemDetails,
            cancellationToken);
    }

    private ValueTask<bool> HandleHttpDependencyAsync(
        HttpContext httpContext,
        HttpRequestException exception,
        CancellationToken cancellationToken)
    {
        logger.LogWarning(
            exception,
            "HTTP dependency request failed for {Method} {Path}.",
            httpContext.Request.Method,
            httpContext.Request.Path);

        var problemDetails = new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = StatusCodes.Status502BadGateway,
            Title = "A downstream service failed.",
            Type = ProblemDetailsDefaults.GetType(StatusCodes.Status502BadGateway),
            Detail = "The request could not be completed because a downstream service failed."
        };

        if (exception.StatusCode.HasValue)
        {
            problemDetails.Extensions["upstreamStatus"] = (int)exception.StatusCode.Value;
        }

        return ProblemDetailsDefaults.WriteAsync(
            problemDetailsService,
            httpContext,
            exception,
            problemDetails,
            cancellationToken);
    }
}
