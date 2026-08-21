using System.Text.Json.Serialization;

namespace ShopInventory.DTOs;

/// <summary>
/// One line of an online van sale, serving two masters: SAP invoices it in the request, and the
/// fiscalisation platform rebuilds the signed receipt from it.
/// </summary>
/// <remarks>
/// The fiscal fields mirror <see cref="VanSalesOfflineSaleItemRequest"/> name for name, because the
/// handset signs one canonical payload per cart and does not know which endpoint it is bound for. They are
/// all optional: a handset older than the signing release sends only <see cref="Code"/>,
/// <see cref="Quantity"/>, <see cref="Price"/> and <see cref="Batches"/>, as it always has.
/// </remarks>
public class VanSalesOrderItemRequest
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// Also the receipt line's name, so it is part of what the platform rebuilds.
    /// </summary>
    /// <remarks>
    /// Absent on older handsets, which is survivable only because they do not stamp either. Required on a
    /// stamped sale, though <b>not</b> for signature reasons: the line name is not a component of the
    /// canonical payload, so omitting it does not change the hash. It fails earlier and harder than that.
    /// <c>ReceiptPreflight</c> blocks a line with no name before the receipt is ever offered, and past
    /// that the platform dereferences it unguarded, so a null is a server-side null-ref rather than a
    /// verification failure. Either way the receipt is unsendable and its number is already spent.
    /// </remarks>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("quantity")]
    public int Quantity { get; set; }

    /// <summary>
    /// The tax-inclusive unit price the receipt was signed over, already rounded to 2dp — not a list
    /// price. The platform recomputes the line total and the whole tax block from it.
    /// </summary>
    [JsonPropertyName("price")]
    public double Price { get; set; }

    [JsonPropertyName("batches")]
    public List<VanSalesOrderItemBatchRequest> Batches { get; set; } = new();

    // --- The tax the line was signed under, from the handset's fiscal lease ---
    //
    // Carried for the receipt only. What SAP is told about tax is unchanged: it derives that from the
    // item and the business partner, as it did before handsets stamped anything.

    [JsonPropertyName("tax_code")]
    public string? TaxCode { get; set; }

    /// <summary>The FDMS tax id the line was signed under.</summary>
    [JsonPropertyName("tax_id")]
    public int? TaxId { get; set; }

    /// <summary>
    /// The rate in force when the receipt was signed. Null and zero are different — null is untaxed and
    /// contributes nothing to the signed payload, zero is a zero rate and contributes "0.00".
    /// </summary>
    [JsonPropertyName("tax_percent")]
    public decimal? TaxPercent { get; set; }

    [JsonPropertyName("hs_code")]
    public string? HsCode { get; set; }

    /// <summary>
    /// The unit the line was sold and priced in. Same field, same reason, as its counterpart on
    /// <see cref="VanSalesOfflineSaleItemRequest"/>: without it a line's quantity cannot be totalled
    /// against another line's.
    /// </summary>
    [JsonPropertyName("uom_code")]
    public string? UoMCode { get; set; }

    /// <summary>
    /// The discount given on this line, as a percentage. Reported, never applied —
    /// <see cref="Price"/> is already net of it.
    /// </summary>
    [JsonPropertyName("discount_percent")]
    public decimal? DiscountPercent { get; set; }
}
