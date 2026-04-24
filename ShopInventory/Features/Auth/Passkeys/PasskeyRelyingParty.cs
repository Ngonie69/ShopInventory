using ErrorOr;
using Fido2NetLib;
using ShopInventory.Common.Errors;

namespace ShopInventory.Features.Auth.Passkeys;

public static class PasskeyRelyingParty
{
    private const string ServerName = "Shop Inventory";

    public static ErrorOr<Fido2> Create(string origin, string rpId)
    {
        if (string.IsNullOrWhiteSpace(origin) || string.IsNullOrWhiteSpace(rpId))
        {
            return Errors.Auth.InvalidPasskeyContext;
        }

        if (!Uri.TryCreate(origin.Trim(), UriKind.Absolute, out var originUri))
        {
            return Errors.Auth.InvalidPasskeyContext;
        }

        var normalizedRpId = rpId.Trim().ToLowerInvariant();
        var normalizedHost = originUri.Host.Trim().ToLowerInvariant();
        var secureContext = originUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                            originUri.IsLoopback;

        if (!secureContext)
        {
            return Errors.Auth.InvalidPasskeyContext;
        }

        if (!normalizedHost.Equals(normalizedRpId, StringComparison.Ordinal) &&
            !normalizedHost.EndsWith($".{normalizedRpId}", StringComparison.Ordinal))
        {
            return Errors.Auth.InvalidPasskeyContext;
        }

        return new Fido2(new Fido2Configuration
        {
            ServerName = ServerName,
            ServerDomain = normalizedRpId,
            Origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                originUri.GetLeftPart(UriPartial.Authority)
            }
        });
    }
}