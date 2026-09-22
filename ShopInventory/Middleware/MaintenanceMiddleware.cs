using System.Globalization;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Security;
using ShopInventory.Features.AppVersion;
using ShopInventory.Features.Maintenance;

namespace ShopInventory.Middleware;

/// <summary>
/// Which of the two places in the pipeline this instance of the lockout is running.
/// </summary>
public enum MaintenanceStage
{
    /// <summary>
    /// Before authentication, where most of the lockout lives.
    /// </summary>
    /// <remarks>
    /// A lockout that only applied to authenticated requests would be a lockout with a hole in it,
    /// and refusing a phone costs nothing that authenticating it first would tell us. It also keeps
    /// a refused request from touching the database, which matters when the database is the thing
    /// under maintenance.
    /// </remarks>
    BeforeAuthentication,

    /// <summary>
    /// After authorization, for the audiences whose answer depends on who is asking.
    /// </summary>
    /// <remarks>
    /// Only the web portal, and only because Admins are exempt from it. After authorization rather
    /// than after authentication because the API's policies name their own schemes: until the
    /// authorization middleware has run them, <c>context.User</c> holds the default scheme's result
    /// alone, and the user's token — the thing the exemption turns on — may not be in it yet.
    /// </remarks>
    AfterAuthorization
}

/// <summary>
/// Stops clients transacting while the system is being worked on.
/// </summary>
/// <remarks>
/// <para>
/// Registered twice, once per <see cref="MaintenanceStage"/>. Each instance handles only the
/// audiences belonging to its stage, so a request is judged exactly once, and the split is a
/// property of the audience — <see cref="MaintenanceAudiences.IsJudgedAfterAuthorization"/> — rather
/// than of where the code happens to sit.
/// </para>
/// <para>
/// It sits next to <c>MobileVersionEnforcementMiddleware</c> and answers the same way: a status
/// code with a flat <c>{ code, message, ... }</c> body, which is the shape every one of the apps
/// already knows how to read. 503 rather than 423 or 403 because it is the one status that says
/// "the server, not you, and not forever", and it carries <c>Retry-After</c>, which the HTTP
/// clients in the apps already honour.
/// </para>
/// </remarks>
public sealed class MaintenanceMiddleware(
    RequestDelegate next,
    ILogger<MaintenanceMiddleware> logger,
    IMaintenanceStore store,
    TimeProvider timeProvider,
    MaintenanceStage stage)
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

        var caller = MaintenanceCaller.FromHeaders(context.Request.Headers);
        if (MaintenanceAudiences.IsJudgedAfterAuthorization(caller.Audience)
            != (stage == MaintenanceStage.AfterAuthorization))
        {
            await next(context);
            return;
        }

        // Only asked where it can be answered. Before authentication the principal is empty, and an
        // empty principal is not an Admin — so this would be false anyway; passing it explicitly
        // says so rather than relying on it.
        var isExemptAdmin = stage == MaintenanceStage.AfterAuthorization
            && MaintenanceAdminExemption.AppliesTo(context.User);

        var decision = MaintenanceGate.Evaluate(
            state,
            timeProvider.GetUtcNow().UtcDateTime,
            caller,
            isExemptAdmin,
            context.Request.Method,
            context.Request.Path.Value ?? string.Empty);

        if (!decision.IsBlocked)
        {
            await next(context);
            return;
        }

        var client = MobileClientRequest.FromHeaders(context.Request.Headers);

        // Every value here but the scope and the audience comes off the request, and a refused
        // request is by definition one somebody may be probing with. Newlines in a header would
        // otherwise let a caller write whole lines of their own into the log this feature is read
        // through during an incident. PolicyKey is already a catalogue value, but AppId behind it
        // is not.
        logger.LogInformation(
            "Refused {Method} {Path} from {App} ({Audience}): maintenance lockout is on ({Scope}). Version={Version}, Device={Device}",
            SensitiveDataSanitizer.SanitizeIdentifierForLog(context.Request.Method),
            SensitiveDataSanitizer.SanitizeIdentifierForLog(context.Request.Path),
            SensitiveDataSanitizer.SanitizeIdentifierForLog(
                caller.PolicyKey ?? client.AppId ?? "an unidentified client"),
            caller.Audience,
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
            audience = caller.Audience.ToString(),
            endsAtUtc = decision.EndsAtUtc,
            retryAfterSeconds = (int)Math.Ceiling(decision.RetryAfter.TotalSeconds),
            checkedAtUtc = timeProvider.GetUtcNow().UtcDateTime
        }, context.RequestAborted);
    }
}

public static class MaintenanceMiddlewareExtensions
{
    public static IApplicationBuilder UseMaintenance(this IApplicationBuilder app, MaintenanceStage stage) =>
        app.UseMiddleware<MaintenanceMiddleware>(stage);
}
