using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace ShopInventory.Web.Common.ProblemDetails;

public sealed class ValidationExceptionHandler(
    ILogger<ValidationExceptionHandler> logger,
    IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    public ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not ValidationException validationException
            || !ProblemDetailsDefaults.ShouldWriteProblemDetails(httpContext))
        {
            return ValueTask.FromResult(false);
        }

        var failures = validationException.Errors
            .Where(failure => failure is not null)
            .ToList();

        var errors = failures
            .GroupBy(failure => NormalizeKey(failure.PropertyName))
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(failure => failure.ErrorMessage)
                    .Where(message => !string.IsNullOrWhiteSpace(message))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal);

        var errorCodes = failures
            .Where(failure => !string.IsNullOrWhiteSpace(failure.ErrorCode))
            .GroupBy(failure => NormalizeKey(failure.PropertyName))
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(failure => failure.ErrorCode)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal);

        logger.LogInformation(
            "Validation failed for {Method} {Path} with {ErrorCount} error(s).",
            httpContext.Request.Method,
            httpContext.Request.Path,
            failures.Count);

        var problemDetails = new ValidationProblemDetails(errors)
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "One or more validation errors occurred.",
            Type = ProblemDetailsDefaults.GetType(StatusCodes.Status400BadRequest),
            Detail = "The request contains validation errors."
        };

        if (errorCodes.Count > 0)
        {
            problemDetails.Extensions["errorCodes"] = errorCodes;
        }

        return ProblemDetailsDefaults.WriteAsync(
            problemDetailsService,
            httpContext,
            exception,
            problemDetails,
            cancellationToken);
    }

    private static string NormalizeKey(string? propertyName)
        => string.IsNullOrWhiteSpace(propertyName) ? "request" : propertyName;
}
