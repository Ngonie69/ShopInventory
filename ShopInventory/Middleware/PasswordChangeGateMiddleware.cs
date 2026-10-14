using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Security;
using ShopInventory.Configuration;
using ShopInventory.Features.Password;

namespace ShopInventory.Middleware;

/// <summary>
/// Holds an account that owes a password change to the endpoints it needs to make that change.
/// </summary>
/// <remarks>
/// <para>
/// This is how the three-month renewal reaches the tills and the handsets. Every sign-in reply says
/// whether a change is owed, but those apps were built before it did and ignore the field. They do
/// each have a change-password screen of their own. So the sign-in is allowed, the token is marked
/// (<see cref="PasswordStanding.ClaimType"/>), and everything except changing the password is
/// refused with a sentence saying why — the operator is stopped, told, and able to fix it on the
/// device in their hand.
/// </para>
/// <para>
/// Refusing the sign-in instead would look tidier and be worse: an app that shows "invalid username
/// or password" for every failed sign-in would tell a van rep their password was wrong when it was
/// only old, and they could not reach the screen that fixes it without first signing in.
/// </para>
/// <para>
/// Switched by <see cref="PasswordPolicySettings.EnforceOnApiClients"/>, read per request so the
/// setting can be turned without a restart. The mark is on the token, so this costs one claim
/// lookup and no database work, and after authorization rather than before because the API's
/// policies name their own schemes — until they have run, <c>context.User</c> may not hold the
/// token yet.
/// </para>
/// </remarks>
public sealed class PasswordChangeGateMiddleware(
    RequestDelegate next,
    IOptionsMonitor<PasswordPolicySettings> settings,
    ILogger<PasswordChangeGateMiddleware> logger)
{
    public const string ErrorCode = "Password.ChangeRequired";

    /// <summary>
    /// What an account owing a change may still call: the routes that change a password, on every
    /// client, and the two an app needs to draw its own profile screen and to sign out.
    /// </summary>
    private static readonly string[] AllowedPrefixes =
    [
        "/api/password/",
        "/api/auth/change-password",
        "/api/auth/logout",
        "/api/auth/me",
        "/api/vansales/auth/password",
        "/api/usermanagement/me"
    ];

    public async Task InvokeAsync(HttpContext context)
    {
        if (!settings.CurrentValue.EnforceOnApiClients)
        {
            await next(context);
            return;
        }

        var reason = context.User.FindFirst(PasswordStanding.ClaimType)?.Value;
        if (reason is null || IsExempt(context))
        {
            await next(context);
            return;
        }

        var standing = new PasswordStanding(default, null, ChangeRequired: true, reason, null);
        var message = standing.Message
            ?? "This account's password has to be changed before it can be used.";

        logger.LogInformation(
            "Refused {Method} {Path} for {Username}: a password change is owed ({Reason})",
            SensitiveDataSanitizer.SanitizeIdentifierForLog(context.Request.Method),
            SensitiveDataSanitizer.SanitizeIdentifierForLog(context.Request.Path.Value),
            SensitiveDataSanitizer.SanitizeIdentifierForLog(context.User.Identity?.Name),
            SensitiveDataSanitizer.SanitizeIdentifierForLog(reason));

        context.Response.StatusCode = StatusCodes.Status403Forbidden;

        // Written as problem details and as the flat { code, message } the maintenance and
        // version refusals use, because the clients do not agree on which they read: the sentence
        // is under every name any of them looks for.
        await context.Response.WriteAsJsonAsync(
            new
            {
                type = "https://tools.ietf.org/html/rfc9110#section-15.5.4",
                title = message,
                status = StatusCodes.Status403Forbidden,
                detail = message,
                code = ErrorCode,
                message,
                passwordChangeRequired = true,
                passwordChangeReason = reason
            },
            options: null,
            contentType: "application/problem+json",
            context.RequestAborted);
    }

    private static bool IsExempt(HttpContext context)
    {
        var endpoint = context.GetEndpoint();

        // Nothing matched, so there is nothing to protect; and an anonymous endpoint is open to
        // everybody, token or no token.
        if (endpoint is null || endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            return true;
        }

        var path = context.Request.Path.Value ?? string.Empty;
        return AllowedPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
}

public static class PasswordChangeGateMiddlewareExtensions
{
    public static IApplicationBuilder UsePasswordChangeGate(this IApplicationBuilder app) =>
        app.UseMiddleware<PasswordChangeGateMiddleware>();
}
