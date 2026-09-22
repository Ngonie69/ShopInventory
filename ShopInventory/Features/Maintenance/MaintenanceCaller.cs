using ShopInventory.Features.AppVersion;

namespace ShopInventory.Features.Maintenance;

/// <summary>
/// Which audience a request belongs to, read off its headers.
/// </summary>
/// <param name="Audience">The one audience this caller falls into.</param>
/// <param name="PolicyKey">
/// The mobile app's catalogue key, when the caller is a phone that named itself. Null otherwise —
/// including for a phone on a build too old to send <c>X-App-Id</c>, which is not the same as not
/// being a phone.
/// </param>
public sealed record MaintenanceCaller(MaintenanceAudience Audience, string? PolicyKey)
{
    /// <summary>The header the Web puts on every call it makes to the API.</summary>
    public const string ClientAppHeaderName = "X-Client-App";

    /// <summary>What the Web calls itself in <see cref="ClientAppHeaderName"/>.</summary>
    /// <remarks>
    /// Duplicated as a literal in <c>ShopInventory.Web/Program.cs</c> rather than shared, because
    /// the Web does not reference this project — the same reason its DTOs are hand-mirrored.
    /// <c>MaintenanceCallerTests</c> pins the spelling on this side and
    /// <c>WebApiClientHeaderTests</c> pins it on the other, so the two cannot drift apart into a
    /// lockout that silently stops covering the portal.
    /// </remarks>
    public const string WebPortalClientApp = "web-portal";

    /// <summary>
    /// Classify a request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Order matters, and it is phones first. A handset is recognised by the headers only the apps
    /// send, and that recognition is shared with the version gate so the two cannot disagree about
    /// what a phone is. Only then does the <c>X-Client-App</c> header get a say, so a caller that
    /// claimed to be the portal while sending Android headers would still be treated as a phone.
    /// </para>
    /// <para>
    /// Everything left over is <see cref="MaintenanceAudience.OtherClients"/>. That is the point:
    /// the classification has no "don't know" answer, because a request that fell through the
    /// bottom would be one no lockout could ever stop.
    /// </para>
    /// </remarks>
    public static MaintenanceCaller FromHeaders(IHeaderDictionary headers)
    {
        var client = MobileClientRequest.FromHeaders(headers);
        if (client.IsMobileApp)
        {
            return new MaintenanceCaller(MaintenanceAudience.MobileApps, client.PolicyKey);
        }

        var clientApp = headers[ClientAppHeaderName].FirstOrDefault();
        if (string.Equals(clientApp?.Trim(), WebPortalClientApp, StringComparison.OrdinalIgnoreCase))
        {
            return new MaintenanceCaller(MaintenanceAudience.WebPortal, null);
        }

        return new MaintenanceCaller(MaintenanceAudience.OtherClients, null);
    }
}
