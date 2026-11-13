using System.Security.Claims;
using ShopInventory.Authentication;
using ShopInventory.Features.AppVersion;
using ShopInventory.Features.Maintenance;
using ShopInventory.Models;

namespace ShopInventory.Services.CallTimings;

/// <summary>Which kind of client sent an inbound request, from what the request itself shows.</summary>
/// <remarks>
/// <para>
/// Read once the request is answered, so the identities the endpoint's policy authenticated are on
/// <see cref="HttpContext.User"/>. The labels are a closed set (the phone apps' catalogue keys, the
/// configured API key names), so the page's caller table cannot grow without bound.
/// </para>
/// <list type="bullet">
/// <item>Phones first, by the same headers the version gate uses (<see cref="MobileClientRequest"/>).</item>
/// <item>ShopInventory.Web names itself in <c>X-Client-App</c>. Its calls carry the API key and, when a
/// member of staff is signed in, their token. A customer portal call carries no user token (portal
/// customers sign in against the Web's database, not this API) and the Web's own background work
/// (cache sweeps, Quartz jobs) carries none either; the Web tells them apart in
/// <see cref="SurfaceHeaderName"/>, which it sends for a page's requests and not for background work.
/// That header is a label for counting only: anybody with the key could send any value, so nothing
/// may be granted on it.</item>
/// <item>The KefShop till sends its user's bearer token and nothing else, so a token with no key and no
/// client header is counted as the till; Swagger with a token would land there too.</item>
/// <item>A key alone is an integration, named by its configured key name.</item>
/// </list>
/// </remarks>
public static class InboundCallerClassifier
{
    public const string Web = "Web";
    public const string CustomerPortal = "Customer portal";
    public const string WebBackground = "Web · background";
    public const string WebWithoutUser = "Web · no user";

    /// <summary>
    /// Which part of the Web a request was made for. Must match <c>WebApiClientIdentity.SurfaceHeaderName</c>
    /// in ShopInventory.Web; <c>CallTimingRecordingTests</c> pins the two together.
    /// </summary>
    public const string SurfaceHeaderName = "X-Web-Surface";

    /// <summary>The <see cref="SurfaceHeaderName"/> value of a customer portal page.</summary>
    public const string CustomerPortalSurface = "customer-portal";
    public const string TillOrBearer = "Till / bearer only";
    public const string Anonymous = "Anonymous";
    public const string HandsetPrefix = "Handset · ";
    public const string KeyPrefix = "Key · ";

    public static string Classify(HttpContext context)
    {
        var headers = context.Request.Headers;
        var mobile = MobileClientRequest.FromHeaders(headers);
        if (mobile.IsMobileApp)
        {
            return SapCallLabels.Intern(HandsetPrefix + (mobile.PolicyKey ?? "unnamed app"));
        }

        var identities = context.User.Identities.Where(identity => identity.IsAuthenticated).ToList();
        var keyName = identities
            .Where(ApiKeyServiceCaller.IsApiKeyIdentity)
            .Select(identity => identity.FindFirst("api_key_name")?.Value ?? identity.Name)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
        var hasUser = identities.Any(identity => !ApiKeyServiceCaller.IsApiKeyIdentity(identity));

        var clientApp = headers[MaintenanceCaller.ClientAppHeaderName].FirstOrDefault();
        if (string.Equals(clientApp?.Trim(), MaintenanceCaller.WebPortalClientApp, StringComparison.OrdinalIgnoreCase))
        {
            var surface = headers[SurfaceHeaderName].FirstOrDefault()?.Trim();
            if (string.Equals(surface, CustomerPortalSurface, StringComparison.OrdinalIgnoreCase))
            {
                return CustomerPortal;
            }

            // No surface at all is work the Web did outside any page; a staff page with nobody on it
            // signed in is the last case, and the key has normally been taken off those.
            return hasUser ? Web
                : string.IsNullOrWhiteSpace(surface) ? WebBackground
                : WebWithoutUser;
        }

        if (keyName is not null)
        {
            return SapCallLabels.Intern(KeyPrefix + Shorten(keyName) + (hasUser ? " + user" : string.Empty));
        }

        if (hasUser)
        {
            return identities.Any(identity => identity.HasClaim(identity.RoleClaimType, ApplicationRoles.VanSalesCustomer))
                ? "Customer ordering app"
                : TillOrBearer;
        }

        return Anonymous;
    }

    private static string Shorten(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= 40 ? trimmed : trimmed[..40];
    }
}
