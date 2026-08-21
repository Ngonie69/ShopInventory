namespace ShopInventory.DTOs;

/// <summary>
/// The close a van handset signed for its own fiscal day, as it arrives from the handset.
/// </summary>
/// <remarks>
/// Snake-cased to match every other van-sales payload, which the handset's serializer expects.
///
/// This is the only route by which a van's day can ever be closed. The platform holds the handset's
/// certificate and not its private key, so it can verify this signature but never produce one; if the
/// handset does not send this, the day stays open and ZIMRA is never told what it sold.
/// </remarks>
public class VanSalesFiscalDayCloseRequest
{
    public int device_id { get; set; }

    public int fiscal_day_no { get; set; }

    /// <summary>Local wall clock, no offset — the date the signature covers is the day's *opening*.</summary>
    public string? fiscal_day_opened_at { get; set; }

    public List<VanSalesFiscalDayCounterDto> counters { get; set; } = [];

    /// <summary>Base64 SHA-256 of the canonical fiscal-day payload the handset signed.</summary>
    public string? signature_hash { get; set; }

    /// <summary>Base64 RSA-PKCS1-SHA256 signature over that same payload.</summary>
    public string? signature_value { get; set; }
}

/// <summary>
/// One counter as the handset totalled it, carried verbatim.
/// </summary>
/// <remarks>
/// Nothing here is normalised on arrival or on the way out. The handset's signature covers these exact
/// values, so re-casing a currency or filling in an absent percentage would produce a close the platform
/// refuses — it recomputes the totals and compares against what was signed.
/// </remarks>
public class VanSalesFiscalDayCounterDto
{
    public string counter_type { get; set; } = string.Empty;

    public string? currency { get; set; }

    public int? tax_id { get; set; }

    /// <summary>Null is untaxed, 0 is zero-rated. They sign differently, so the distinction survives.</summary>
    public decimal? tax_percent { get; set; }

    public string? money_type { get; set; }

    public decimal value { get; set; }
}

/// <summary>What the handset is told once its close is safely held.</summary>
public class VanSalesFiscalDayCloseResponse
{
    public bool accepted { get; set; }

    public int device_id { get; set; }

    public int fiscal_day_no { get; set; }

    /// <summary>
    /// True when this close was already held. A handset that loses the response re-sends, and the second
    /// arrival is routine rather than an error.
    /// </summary>
    public bool duplicate { get; set; }

    public string? message { get; set; }
}
