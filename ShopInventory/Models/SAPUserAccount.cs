using System.Text.Json.Serialization;

namespace ShopInventory.Models;

/// <summary>
/// A SAP Business One user account (OUSR) as the Service Layer's <c>Users</c> entity set returns it,
/// with the fields an administrator needs to see who is locked out and when their password last
/// changed.
/// </summary>
/// <remarks>
/// Wider than <see cref="SAPUser"/>, which exists only to put a name against the <c>InternalKey</c>
/// the approval collections carry, and deliberately not merged with it: the approval lists read a
/// name per row through a cache, and widening that read would pull each user's lock state and mail
/// address across the wire dozens of times for a label.
/// <para>
/// Dates and times are strings because that is what the Service Layer sends and what every other SAP
/// model here does — <c>Edm.Time</c> in particular comes back in a shape that varies by patch level,
/// and a parse failure here would cost the whole page rather than one column.
/// </para>
/// </remarks>
public class SAPUserAccount
{
    /// <summary>The key SAP identifies the user by; what a PATCH addresses.</summary>
    [JsonPropertyName("InternalKey")]
    public int InternalKey { get; set; }

    /// <summary>The login code, e.g. <c>manager</c>. Unique in the company.</summary>
    [JsonPropertyName("UserCode")]
    public string? UserCode { get; set; }

    [JsonPropertyName("UserName")]
    public string? UserName { get; set; }

    /// <summary>Lower-cased first letter in SAP's own metadata, not a typo.</summary>
    [JsonPropertyName("eMail")]
    public string? EMail { get; set; }

    /// <summary>tYES or tNO. tYES is the account SAP will not let sign in.</summary>
    [JsonPropertyName("Locked")]
    public string? Locked { get; set; }

    /// <summary>tYES or tNO. A superuser holds every authorisation in the company.</summary>
    [JsonPropertyName("Superuser")]
    public string? Superuser { get; set; }

    /// <summary>The <c>UserCode</c> of whoever last set this user's password.</summary>
    [JsonPropertyName("LastPasswordChangedBy")]
    public string? LastPasswordChangedBy { get; set; }

    /// <summary>The last time the user signed out, as SAP formats it.</summary>
    [JsonPropertyName("LastLogoutDate")]
    public string? LastLogoutDate { get; set; }

    public bool IsLocked => string.Equals(Locked, SapYesNo.Yes, StringComparison.OrdinalIgnoreCase);

    public bool IsSuperuser => string.Equals(Superuser, SapYesNo.Yes, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Bounds on the SAP user administration reads, shared by the client and its callers.</summary>
public static class SapUserAccountLimits
{
    /// <summary>
    /// The most accounts one read returns. SAP pages at 20 without a <c>Prefer</c> header regardless
    /// of <c>$top</c>, so the two always travel together. A B1 company has tens of users; this is a
    /// guard against a surprise, not a paging scheme.
    /// </summary>
    public const int PageSize = 200;
}
