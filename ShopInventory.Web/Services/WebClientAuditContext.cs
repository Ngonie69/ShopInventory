using Microsoft.AspNetCore.Http;

namespace ShopInventory.Web.Services;

public sealed class WebClientAuditContext
{
    public string? IpAddress { get; private set; }
    public string? UserAgent { get; private set; }
    public DateTime? CapturedAtUtc { get; private set; }

    public void Capture(HttpContext? httpContext)
    {
        if (httpContext is null)
        {
            return;
        }

        Capture(
            httpContext.Connection.RemoteIpAddress?.ToString(),
            httpContext.Request.Headers.UserAgent.ToString());
    }

    public void Capture(string? ipAddress, string? userAgent)
    {
        IpAddress = Normalize(ipAddress);
        UserAgent = Normalize(userAgent);
        CapturedAtUtc = DateTime.UtcNow;
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }
}
