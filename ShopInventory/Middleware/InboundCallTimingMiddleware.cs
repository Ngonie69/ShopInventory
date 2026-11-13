using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using ShopInventory.Services.CallTimings;

namespace ShopInventory.Middleware;

/// <summary>
/// Times every inbound API request for the Admin call timings page: its route template, method, status,
/// duration, which kind of client sent it, and the SAP time it spent.
/// </summary>
/// <remarks>
/// <para>
/// The route is the matched endpoint's template (<c>api/Invoice/{docEntry}</c>), never the request path,
/// so ids, card codes and warehouse codes are not kept. A request that matched no endpoint is kept as
/// <c>(no route)</c>. SignalR hub connections are skipped: a connection lasts as long as the page that
/// opened it, and would read as the slowest route of the day.
/// </para>
/// <para>
/// Near the front of the pipeline, before the exception handler and the status-code pages, so it times
/// the whole request and records the status the caller actually got. The endpoint is already known
/// there: WebApplication matches routes before the first middleware runs.
/// </para>
/// </remarks>
public sealed class InboundCallTimingMiddleware(RequestDelegate next, CallTimingRecorder recorder)
{
    public const string NoRoute = "(no route)";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!recorder.Enabled)
        {
            await next(context);
            return;
        }

        var endpoint = context.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<HubMetadata>() is not null)
        {
            await next(context);
            return;
        }

        var clock = recorder.TimeProvider;
        var startedUtc = clock.GetUtcNow().UtcDateTime;
        var started = clock.GetTimestamp();
        var method = context.Request.Method;
        var route = RouteOf(endpoint);
        var call = new ApiCallContext(SapCallLabels.Intern($"{method} {route}"));
        var failed = false;

        using (ApiCallContext.Begin(call))
        {
            try
            {
                await next(context);
            }
            catch
            {
                failed = true;
                throw;
            }
            finally
            {
                var elapsed = clock.GetElapsedTime(started);
                var status = failed && !context.Response.HasStarted
                    ? StatusCodes.Status500InternalServerError
                    : context.Response.StatusCode;

                recorder.Finished(new InboundCallRecord(
                    startedUtc.Ticks,
                    (int)Math.Clamp(Math.Round(elapsed.TotalMilliseconds), 0, int.MaxValue),
                    (short)status,
                    method,
                    route,
                    InboundCallerClassifier.Classify(context),
                    call.SapCalls,
                    call.SapMs));
            }
        }
    }

    /// <summary>The endpoint's route template, or <see cref="NoRoute"/>.</summary>
    public static string RouteOf(Endpoint? endpoint) =>
        endpoint is RouteEndpoint { RoutePattern.RawText: { Length: > 0 } template }
            ? SapCallLabels.Intern(template.TrimStart('/'))
            : NoRoute;
}

public static class InboundCallTimingMiddlewareExtensions
{
    public static IApplicationBuilder UseInboundCallTiming(this IApplicationBuilder builder) =>
        builder.UseMiddleware<InboundCallTimingMiddleware>();
}
