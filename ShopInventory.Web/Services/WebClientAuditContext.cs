using Microsoft.AspNetCore.Http;
using System.Net;

namespace ShopInventory.Web.Services;

public sealed class WebClientAuditContext
{
    public string? IpAddress { get; private set; }
    public string? UserAgent { get; private set; }
    public DateTime? CapturedAtUtc { get; private set; }

    public string? ForwardableIpAddress => IsMeaningfulClientAddress(IpAddress) ? IpAddress : null;

    public void Capture(HttpContext? httpContext)
    {
        if (httpContext is null)
        {
            return;
        }

        Capture(
            ResolveClientIpAddress(httpContext),
            httpContext.Request.Headers.UserAgent.ToString());
    }

    public void Capture(string? ipAddress, string? userAgent)
    {
        var normalizedIpAddress = NormalizeIpAddress(ipAddress);
        var normalizedUserAgent = Normalize(userAgent);

        // A Blazor circuit can reconnect through an internal loopback connection. Do not let
        // that later event replace a client address captured when the circuit was opened.
        if (IsMeaningfulClientAddress(normalizedIpAddress) || !IsMeaningfulClientAddress(IpAddress))
        {
            IpAddress = normalizedIpAddress;
        }

        if (normalizedUserAgent is not null)
        {
            UserAgent = normalizedUserAgent;
        }

        CapturedAtUtc = DateTime.UtcNow;
    }

    private static string? ResolveClientIpAddress(HttpContext httpContext)
    {
        var remoteAddress = NormalizeAddress(httpContext.Connection.RemoteIpAddress);
        if (remoteAddress is null)
        {
            return null;
        }

        if (!IPAddress.IsLoopback(remoteAddress))
        {
            return remoteAddress.ToString();
        }

        // Only trust client-supplied forwarding headers when the immediate peer is local.
        // This covers IIS/ARR and a same-host reverse proxy without accepting spoofed headers
        // from a device connected directly to the application.
        foreach (var headerName in new[] { "X-Forwarded-For", "X-Real-IP" })
        {
            var values = httpContext.Request.Headers[headerName].ToString();
            foreach (var value in values.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (TryParseAddress(value, out var forwardedAddress) && !IPAddress.IsLoopback(forwardedAddress))
                {
                    return forwardedAddress.ToString();
                }
            }
        }

        return remoteAddress.ToString();
    }

    private static string? NormalizeIpAddress(string? value)
        => TryParseAddress(value, out var address) ? address.ToString() : Normalize(value);

    private static bool IsMeaningfulClientAddress(string? value)
        => TryParseAddress(value, out var address) && !IPAddress.IsLoopback(address);

    private static bool TryParseAddress(string? value, out IPAddress address)
    {
        address = IPAddress.None;
        var candidate = Normalize(value)?.Trim('"');
        if (candidate is null)
        {
            return false;
        }

        if (candidate.StartsWith('[') && candidate.IndexOf(']') is var closingBracket && closingBracket > 0)
        {
            candidate = candidate[1..closingBracket];
        }
        else if (!IPAddress.TryParse(candidate, out _) && candidate.Count(character => character == ':') == 1)
        {
            candidate = candidate[..candidate.LastIndexOf(':')];
        }

        if (!IPAddress.TryParse(candidate, out var parsedAddress))
        {
            return false;
        }

        address = NormalizeAddress(parsedAddress)!;
        return true;
    }

    private static IPAddress? NormalizeAddress(IPAddress? address)
        => address?.IsIPv4MappedToIPv6 == true ? address.MapToIPv4() : address;

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }
}
