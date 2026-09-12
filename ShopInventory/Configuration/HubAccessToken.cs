namespace ShopInventory.Configuration;

/// <summary>
/// The bearer token a SignalR client sends in the query string, for the hub paths only.
/// </summary>
/// <remarks>
/// A WebSocket handshake is a browser <c>WebSocket</c> open, not an XHR, so it carries no headers a
/// caller can set. SignalR's answer is <c>?access_token=</c>, and a server that does not read it can
/// never authenticate a browser client against an <c>[Authorize]</c> hub. Our own .NET clients set the
/// header instead and are unaffected, which is why this went unnoticed.
///
/// <para>
/// Restricted to the hub paths on purpose. A token in a query string is a token in proxy logs, browser
/// history and Referer headers, so it is accepted only where the transport leaves no alternative, and
/// the header remains the way everything else authenticates.
/// </para>
/// </remarks>
public static class HubAccessToken
{
    /// <summary>The hub is mapped at both of these; see <c>Program.cs</c>.</summary>
    public static bool IsHubPath(PathString path) =>
        path.StartsWithSegments("/hubs") || path.StartsWithSegments("/api/hubs");

    /// <summary>
    /// The token to authenticate this request with, or <c>null</c> to leave the request as it is —
    /// which lets the <c>Authorization</c> header speak for itself.
    /// </summary>
    public static string? FromQuery(PathString path, IQueryCollection query)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!IsHubPath(path))
        {
            return null;
        }

        var token = query["access_token"].ToString();

        return string.IsNullOrWhiteSpace(token) ? null : token;
    }
}
