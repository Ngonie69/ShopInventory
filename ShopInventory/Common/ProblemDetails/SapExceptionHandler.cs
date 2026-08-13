using Microsoft.AspNetCore.Diagnostics;
using ShopInventory.Common.Security;
using ShopInventory.Services;

namespace ShopInventory.Common.ProblemDetails;

public sealed class SapExceptionHandler(
    ILogger<SapExceptionHandler> logger,
    IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    public ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        return exception switch
        {
            SapPostingPeriodException postingPeriodException => HandlePostingPeriodAsync(
                httpContext,
                postingPeriodException,
                cancellationToken),
            SapCircuitOpenException sapCircuitOpenException => HandleCircuitOpenAsync(
                httpContext,
                sapCircuitOpenException,
                cancellationToken),
            _ => ValueTask.FromResult(false)
        };
    }

    private ValueTask<bool> HandlePostingPeriodAsync(
        HttpContext httpContext,
        SapPostingPeriodException exception,
        CancellationToken cancellationToken)
    {
        logger.LogWarning(
            exception,
            "SAP posting period rejected request {Method} {Path} for document date {DocDate}.",
            SensitiveDataSanitizer.SanitizeIdentifierForLog(httpContext.Request.Method),
            SensitiveDataSanitizer.SanitizeIdentifierForLog(httpContext.Request.Path),
            exception.DocDate);

        var problemDetails = new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = StatusCodes.Status409Conflict,
            Title = "SAP posting period is not open.",
            Type = ProblemDetailsDefaults.GetType(StatusCodes.Status409Conflict),
            Detail = exception.Message
        };
        problemDetails.Extensions["docDate"] = exception.DocDate;

        if (!string.IsNullOrWhiteSpace(exception.SapError))
        {
            problemDetails.Extensions["sapError"] = exception.SapError;
        }

        return ProblemDetailsDefaults.WriteAsync(
            problemDetailsService,
            httpContext,
            exception,
            problemDetails,
            cancellationToken);
    }

    private ValueTask<bool> HandleCircuitOpenAsync(
        HttpContext httpContext,
        SapCircuitOpenException exception,
        CancellationToken cancellationToken)
    {
        logger.LogWarning(
            exception,
            "SAP circuit is open for {Method} {Path}.",
            SensitiveDataSanitizer.SanitizeIdentifierForLog(httpContext.Request.Method),
            SensitiveDataSanitizer.SanitizeIdentifierForLog(httpContext.Request.Path));

        var problemDetails = new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = StatusCodes.Status503ServiceUnavailable,
            Title = "SAP is temporarily unavailable.",
            Type = ProblemDetailsDefaults.GetType(StatusCodes.Status503ServiceUnavailable),
            Detail = exception.Message
        };
        problemDetails.Extensions["retryable"] = true;

        return ProblemDetailsDefaults.WriteAsync(
            problemDetailsService,
            httpContext,
            exception,
            problemDetails,
            cancellationToken);
    }
}
