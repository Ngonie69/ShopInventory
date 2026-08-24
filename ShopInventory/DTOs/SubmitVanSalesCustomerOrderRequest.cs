using System.ComponentModel.DataAnnotations;

namespace ShopInventory.DTOs;

/// <summary>One item on an order the app is sending.</summary>
public class SubmitVanSalesCustomerOrderLineRequest
{
    [Required]
    [MaxLength(50)]
    public string ItemCode { get; set; } = string.Empty;

    public decimal Quantity { get; set; }
}

/// <summary>
/// An order a van sales customer is placing from the app.
/// </summary>
/// <remarks>
/// There is no price on this request, and that is deliberate: the app displays a cached catalogue
/// that may be days old, and what the customer pays is decided by the server against the current
/// price list. The priced order comes back on the response for the app to show.
/// </remarks>
public class SubmitVanSalesCustomerOrderRequest
{
    /// <summary>
    /// The app's idempotency key for this order — a GUID minted when the draft was created, not
    /// when it was sent.
    /// </summary>
    /// <remarks>
    /// Sending the same key twice returns the same order rather than creating a second one. This is
    /// what makes retrying safe from a place with no signal, and it only works if the key is fixed
    /// at draft time: a key generated at send time is a new key on every retry, and every retry a
    /// new delivery.
    /// </remarks>
    [Required]
    [MaxLength(100)]
    public string ClientRequestId { get; set; } = string.Empty;

    [Required]
    public List<SubmitVanSalesCustomerOrderLineRequest> Lines { get; set; } = [];

    /// <summary>
    /// The delivery this order is for. Omit to take the next one still open.
    /// </summary>
    /// <remarks>
    /// Worth sending for a queued order: it says which call the customer meant when they built it,
    /// and the server refuses it rather than quietly moving stock onto a van that has already left.
    /// </remarks>
    public DateTime? RequestedVisitDate { get; set; }

    [MaxLength(1000)]
    public string? CustomerNotes { get; set; }

    /// <summary>When the customer pressed send, by the handset's clock. Recorded, never trusted.</summary>
    public DateTime? SubmittedAtUtc { get; set; }

    [MaxLength(200)]
    public string? DeviceInfo { get; set; }

    [MaxLength(50)]
    public string? AppVersion { get; set; }

    public double? Latitude { get; set; }

    public double? Longitude { get; set; }
}

/// <summary>A customer withdrawing an order.</summary>
public class CancelVanSalesCustomerOrderRequest
{
    [MaxLength(500)]
    public string? Reason { get; set; }
}
