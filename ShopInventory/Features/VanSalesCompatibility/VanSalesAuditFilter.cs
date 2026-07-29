using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesCompatibility;

public sealed class VanSalesAuditFilter(
    IAuditService auditService,
    ILogger<VanSalesAuditFilter> logger
) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ActionExecutedContext? executedContext = null;
        Exception? pipelineException = null;

        try
        {
            executedContext = await next();
        }
        catch (Exception ex)
        {
            pipelineException = ex;
            throw;
        }
        finally
        {
            try
            {
                var request = context.HttpContext.Request;
                var path = request.Path.Value ?? "/api/vansales";
                var statusCode = ResolveStatusCode(executedContext, pipelineException);
                var isSuccess = pipelineException is null && statusCode < StatusCodes.Status400BadRequest;
                var errorMessage = ResolveErrorMessage(executedContext, pipelineException);
                var details = $"{request.Method.ToUpperInvariant()} {path} returned {statusCode}.";

                await auditService.LogAsync(
                    ResolveActionName(context, request.Method),
                    "VanSalesEndpoint",
                    path,
                    details,
                    isSuccess,
                    errorMessage);
            }
            catch (Exception auditException)
            {
                logger.LogWarning(
                    auditException,
                    "Failed to audit van sales request {Method} {Path}",
                    context.HttpContext.Request.Method,
                    context.HttpContext.Request.Path);
            }
        }
    }

    private static string ResolveActionName(ActionExecutingContext context, string method)
    {
        if (context.ActionDescriptor.RouteValues.TryGetValue("action", out var actionName) &&
            !string.IsNullOrWhiteSpace(actionName))
        {
            return $"VanSales{actionName}";
        }

        return $"VanSales{NormalizeToken(method)}";
    }

    private static int ResolveStatusCode(ActionExecutedContext? context, Exception? pipelineException)
    {
        if (pipelineException is not null)
        {
            return StatusCodes.Status500InternalServerError;
        }

        if (context?.Exception is not null && !context.ExceptionHandled)
        {
            return StatusCodes.Status500InternalServerError;
        }

        if (context?.Result is ObjectResult objectResult && objectResult.StatusCode.HasValue)
        {
            return objectResult.StatusCode.Value;
        }

        if (context?.Result is StatusCodeResult statusCodeResult)
        {
            return statusCodeResult.StatusCode;
        }

        return context?.HttpContext.Response.StatusCode is > 0
            ? context.HttpContext.Response.StatusCode
            : StatusCodes.Status200OK;
    }

    private static string? ResolveErrorMessage(ActionExecutedContext? context, Exception? pipelineException)
    {
        if (pipelineException is not null)
        {
            return pipelineException.Message;
        }

        if (context?.Exception is not null && !context.ExceptionHandled)
        {
            return context.Exception.Message;
        }

        if (context?.Result is ObjectResult { Value: ProblemDetails problemDetails })
        {
            return string.IsNullOrWhiteSpace(problemDetails.Detail)
                ? problemDetails.Title
                : problemDetails.Detail;
        }

        return null;
    }

    private static string NormalizeToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Request";
        }

        return char.ToUpperInvariant(value[0]) + value[1..];
    }
}