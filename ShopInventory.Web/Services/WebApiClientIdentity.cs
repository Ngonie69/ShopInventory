namespace ShopInventory.Web.Services;

/// <summary>
/// How the Web names itself on every call it makes to the API.
/// </summary>
/// <remarks>
/// <para>
/// The API's maintenance lockout decides what to refuse by audience, and it can only tell the
/// portal from a till or an integration if the portal says so. Nothing else it already sends
/// identifies it: the API key is shared with the background jobs and the customer portal, and the
/// absence of the Android headers only rules out a phone.
/// </para>
/// <para>
/// Both spellings are literals — this one and <c>MaintenanceCaller.WebPortalClientApp</c> in the
/// API — because the Web does not reference that project, which is the same reason its DTOs are
/// hand-mirrored. Two literals can drift, and the drift would be silent and one-directional: the
/// lockout would simply stop covering the portal, with the settings screen still showing the tick.
/// <c>WebApiClientHeaderTests</c> pins the value and pins that every API client carries it.
/// </para>
/// </remarks>
public static class WebApiClientIdentity
{
    public const string ClientAppHeaderName = "X-Client-App";

    /// <summary>Must match <c>ShopInventory.Features.Maintenance.MaintenanceCaller.WebPortalClientApp</c>.</summary>
    public const string ClientAppValue = "web-portal";

    /// <summary>
    /// Which part of the Web a request was made for, sent by <see cref="WebSurfaceHandler"/>: the
    /// customer portal or a staff page. Absent on the Web's own background work (Quartz jobs, cache
    /// sweeps), which runs outside any circuit.
    /// </summary>
    /// <remarks>
    /// Read by the API's call timings to tell the customer portal from the Web's background work: both
    /// call with the key and no user token, so nothing else on the request separates them. Not an
    /// authorisation signal — anybody holding the key could send any value — and the API must never
    /// treat it as one. Must match <c>InboundCallerClassifier.SurfaceHeaderName</c> in the API.
    /// </remarks>
    public const string SurfaceHeaderName = "X-Web-Surface";

    /// <summary>A request made for a <c>/customer-portal</c> page.</summary>
    public const string CustomerPortalSurface = "customer-portal";

    /// <summary>A request made for any other page: the staff app.</summary>
    public const string StaffSurface = "staff";

    /// <summary>Put the portal's name on a client the Web uses to reach the API.</summary>
    public static void IdentifyAsWebPortal(HttpClient client) =>
        client.DefaultRequestHeaders.Add(ClientAppHeaderName, ClientAppValue);
}
