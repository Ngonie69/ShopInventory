using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using ShopInventory.Services;

namespace ShopInventory.Common.Auditing;

/// <summary>
/// Writes one audit row for every request that reaches an action on the controller it is applied to.
/// </summary>
/// <remarks>
/// <para>
/// A blanket filter rather than a call in each handler, because the alternative is a list of audited
/// endpoints that drifts from the controller: a new action added next year is audited here by virtue
/// of being on the controller at all, and nobody has to remember. Handlers still log their own rows
/// where the coarse one cannot answer the question — the row this writes says an endpoint was called
/// and how it answered, not which document it moved or for how much.
/// </para>
/// <para>
/// The row is written through its own DI scope, so the audit's <c>SaveChanges</c> runs on a context of
/// its own. Sharing the request's context would make this a commit point for whatever the handler left
/// tracked and unsaved — on an error path, exactly the changes it decided not to keep.
/// </para>
/// <para>
/// It runs as an action filter, so it sees only requests that reached the action. Authorization
/// refusals, the <c>[ApiController]</c> model-state 400 and rate-limit rejections all short-circuit
/// ahead of it and are not audited here.
/// </para>
/// </remarks>
public abstract class EndpointAuditFilter(
    IServiceScopeFactory scopeFactory,
    ILogger logger
) : IAsyncActionFilter
{
    /// <summary>Prepended to the action name, so one surface's rows are filterable as a set.</summary>
    protected abstract string ActionPrefix { get; }

    /// <summary>Recorded as the row's entity type. Names the surface, not a document type.</summary>
    protected abstract string EntityType { get; }

    /// <summary>Used when the request carries no path, which only happens in tests.</summary>
    protected abstract string FallbackPath { get; }

    /// <summary>
    /// Whether this request is worth a row. Defaults to all of them; override to keep polling reads
    /// out of the trail.
    /// </summary>
    protected virtual bool ShouldAudit(HttpRequest request) => true;

    /// <summary>
    /// Whether the row's details carry the query string. Off by default, because a query string can
    /// carry anything a caller chose to put there; a surface turns it on when its parameters are what
    /// make a row worth reading — a report's period and subject, say.
    /// </summary>
    protected virtual bool RecordQueryString => false;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!ShouldAudit(context.HttpContext.Request))
        {
            await next();
            return;
        }

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
                var path = request.Path.Value ?? FallbackPath;
                var statusCode = ResolveStatusCode(executedContext, pipelineException);
                var isSuccess = pipelineException is null && statusCode < StatusCodes.Status400BadRequest;
                var errorMessage = ResolveErrorMessage(executedContext, pipelineException);
                var query = RecordQueryString ? request.QueryString.Value : null;
                var details = $"{request.Method.ToUpperInvariant()} {path}{query} returned {statusCode}.";

                await using var scope = scopeFactory.CreateAsyncScope();
                var auditService = scope.ServiceProvider.GetRequiredService<IAuditService>();

                await auditService.LogAsync(
                    ResolveActionName(context, request.Method),
                    EntityType,
                    path,
                    details,
                    isSuccess,
                    errorMessage);
            }
            catch (Exception auditException)
            {
                logger.LogWarning(
                    auditException,
                    "Failed to audit request {Method} {Path}",
                    context.HttpContext.Request.Method,
                    context.HttpContext.Request.Path);
            }
        }
    }

    /// <summary>
    /// True for the methods that change something. The read side of these surfaces is a till or a
    /// handset polling stock and queue state, which buries the rows worth reading.
    /// </summary>
    protected static bool IsMutating(HttpRequest request)
        => HttpMethods.IsPost(request.Method)
            || HttpMethods.IsPut(request.Method)
            || HttpMethods.IsPatch(request.Method)
            || HttpMethods.IsDelete(request.Method);

    private string ResolveActionName(ActionExecutingContext context, string method)
    {
        if (context.ActionDescriptor.RouteValues.TryGetValue("action", out var actionName) &&
            !string.IsNullOrWhiteSpace(actionName))
        {
            return $"{ActionPrefix}{actionName}";
        }

        return $"{ActionPrefix}{NormalizeToken(method)}";
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

        if (context?.Result is ObjectResult { Value: Microsoft.AspNetCore.Mvc.ProblemDetails problemDetails })
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
