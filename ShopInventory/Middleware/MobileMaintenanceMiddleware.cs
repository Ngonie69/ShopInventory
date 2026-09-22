using System.Globalization;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Security;
using ShopInventory.Features.AppVersion;
using ShopInventory.Features.Maintenance;

namespace ShopInventory.Middleware;

/// <summary>
/// Stops the Android apps transacting while the system is being worked on.
/// </summary>
/// <remarks>
/// <para>
/// This runs before authentication, deliberately. A lockout that only applied to authenticated
/// requests would be a lockout with a hole in it, and refusing a phone costs nothing that
/// authenticating it first would tell us — the decision turns on the app's headers and the
/// operator's switch, not on who is holding the handset.
/// </para>
/// <para>
/// It sits next to <c>MobileVersionEnforcementMiddleware</c> and answers the same way: a status
/// code with a flat <c>{ code, message, ... }</c> body, which is the shape every one of the apps
/// already knows how to read. 503 rather than 423 or 403 because it is the one status that says
/// "the server, not you, and not forever", and it carries <c>Retry-After</c>, which the HTTP
/// clients in the apps already honour.
/// </para>
/// </remarks>
public sealed class MobileMaintenanceMiddleware(
    RequestDelegate next,
    ILogger<MobileMaintenanceMiddleware> logger,
    IMobileMaintenanceStore store,
    TimeProvider timeProvider)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var state = store.Current;

        // The overwhelmingly common case: no maintenance running. One volatile field read, no
        // header parsing, no allocation.
        if (!state.Enabled)
        {
            await next(context);
            return;
        }

        var client = MobileClientRequest.FromHeaders(context.Request.Headers);
        var decision = MobileMaintenanceGate.Evaluate(
            state,
            timeProvider.GetUtcNow().UtcDateTime,
            client.IsMobileApp,
            client.PolicyKey,
            context.Request.Method,
            context.Request.Path.Value ?? string.Empty);

        if (!decision.IsBlocked)
        {
            await next(context);
            return;
        }

        // Every value here but the scope comes off the request, and a refused request is by
        // definition one somebody may be probing with. Newlines in a header would otherwise let a
        // caller write whole lines of their own into the log this feature is read through during an
        // incident. PolicyKey is already a catalogue value, but AppId behind it is not.
        logger.LogInformation(
            "Refused {Method} {Path} from {App}: mobile maintenance lockout is on ({Scope}). Version={Version}, Device={Device}",
            SensitiveDataSanitizer.SanitizeIdentifierForLog(context.Request.Method),
            SensitiveDataSanitizer.SanitizeIdentifierForLog(context.Request.Path),
            SensitiveDataSanitizer.SanitizeIdentifierForLog(
                client.PolicyKey ?? client.AppId ?? "an unidentified app"),
            decision.Scope,
            SensitiveDataSanitizer.SanitizeIdentifierForLog(client.Version),
            SensitiveDataSanitizer.SanitizeIdentifierForLog(client.DeviceModel));

        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter =
            ((int)Math.Ceiling(decision.RetryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

        await context.Response.WriteAsJsonAsync(new
        {
            code = Errors.Maintenance.MobileTransactionsSuspended.Code,
            message = decision.Message,
            maintenance = true,
            scope = decision.Scope.ToString(),
            endsAtUtc = decision.EndsAtUtc,
            retryAfterSeconds = (int)Math.Ceiling(decision.RetryAfter.TotalSeconds),
            checkedAtUtc = timeProvider.GetUtcNow().UtcDateTime
        }, context.RequestAborted);
    }
}

public static class MobileMaintenanceMiddlewareExtensions
{
    public static IApplicationBuilder UseMobileMaintenance(this IApplicationBuilder app) =>
        app.UseMiddleware<MobileMaintenanceMiddleware>();
}
