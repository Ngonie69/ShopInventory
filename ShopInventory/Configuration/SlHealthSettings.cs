namespace ShopInventory.Configuration;

/// <summary>
/// Where to ask SL Health what state the SAP Service Layer is in.
/// </summary>
/// <remarks>
/// SL Health watches the same Service Layer this API posts to, and knows what the API cannot work out
/// from its own failed calls: that SAP is in a planned maintenance window, that it is being restarted,
/// that its logins are failing while a ping still answers. Both keys empty, which is the default, and
/// SL Health is never asked: the API behaves exactly as it did before it could ask.
/// </remarks>
public sealed class SlHealthSettings
{
    public const string SectionName = "SlHealth";

    /// <summary>SL Health's address, for example <c>http://10.10.10.9:5080</c>. Empty turns the reader off.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// The token SL Health lists for this application under <c>Integration:Clients</c>. Sent as a bearer
    /// token and never logged. Empty turns the reader off.
    /// </summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>The address to ask, or null when the reader is off or the address cannot be used.</summary>
    public Uri? BaseAddress() =>
        !string.IsNullOrWhiteSpace(Token)
        && Uri.TryCreate(Url?.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var address)
        && (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps)
            ? address
            : null;

    /// <summary>Whether SL Health is asked at all.</summary>
    public bool IsConfigured => BaseAddress() is not null;
}
