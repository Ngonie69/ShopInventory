using Microsoft.AspNetCore.Http;
using ShopInventory.Common.Security;

namespace ShopInventory.Features.SapUsers;

/// <summary>
/// Who an audit row for a SAP user change should name.
/// </summary>
/// <remarks>
/// Read from the account rather than from <c>User.Identity.Name</c>. ShopInventory.Web sends its
/// integration key and the signed-in user's token on the same request, and the key's identity comes
/// first — so the name claim on the merged principal is the key's, and every one of these rows
/// would be signed "MainIntegration" instead of the person who pressed the button. That is the one
/// fact an audit of an unlock exists to record.
/// </remarks>
internal static class SapUserAuditActor
{
    /// <summary>The key calling on its own, with no user behind it.</summary>
    public const string ServiceCaller = "an integration key";

    /// <summary>When the request names a user this application cannot resolve to an account.</summary>
    public const string Unknown = "an unidentified caller";

    public static async Task<string> ResolveAsync(
        ICallerAccountReader callerAccounts,
        IHttpContextAccessor httpContextAccessor,
        CancellationToken cancellationToken)
    {
        var caller = await callerAccounts.ReadAsync(httpContextAccessor.HttpContext?.User, cancellationToken);

        if (caller.IsError)
        {
            return Unknown;
        }

        return caller.Value.IsServiceCaller
            ? ServiceCaller
            : caller.Value.Username ?? Unknown;
    }
}
