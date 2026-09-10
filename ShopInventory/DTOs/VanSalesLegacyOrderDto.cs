using System.Text.Json.Serialization;

namespace ShopInventory.DTOs;

public class VanSalesLegacyOrderDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("customer_id")]
    public int CustomerId { get; set; }

    [JsonPropertyName("ref")]
    public string Reference { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = string.Empty;

    [JsonPropertyName("item")]
    public int Item { get; set; }

    [JsonPropertyName("units")]
    public int Units { get; set; }

    /// <summary>The document total before tax.</summary>
    [JsonPropertyName("price")]
    public double Price { get; set; }

    /// <summary>
    /// The document total the customer is billed — tax included, as the server worked it out.
    /// </summary>
    /// <remarks>
    /// Only the net used to be sent, so the handset added the tax back itself, at a rate it guesses
    /// when it has no fiscal lease. Two things went wrong with that and only one of them is a cent.
    ///
    /// <para>The cent: <c>SalesOrderLineEntity.LineTotal</c> is <c>decimal(18,2)</c> where
    /// <c>UnitPrice</c> is <c>decimal(18,4)</c>, so a line of 1 × 0.5455 is stored as 0.55. Taxing
    /// that rounded net crosses a rounding midpoint the true net does not —
    /// <c>Round(0.55 × 15.5%) = 0.09</c> against <c>Round(0.5455 × 15.5%) = 0.08</c> — and SO3442 came
    /// out a cent over on the handset's own list.</para>
    ///
    /// <para>The larger one: the rate. A handset with no lease falls back to a fixed percentage, so a
    /// line carrying any other rate is restated at the wrong one. The server holds the real figure
    /// either way, and this is it, so the handset no longer has to derive what is already known.</para>
    ///
    /// <para>Null rather than zero when a caller cannot supply it, because a handset must be able to
    /// tell "the server did not say" from "this order is worth nothing" — the old derivation is what
    /// it falls back to, and a zero here would silently price every order at nothing.</para>
    /// </remarks>
    [JsonPropertyName("gross")]
    public double? Gross { get; set; }

    [JsonPropertyName("doc_date")]
    public string DocDate { get; set; } = string.Empty;

    [JsonPropertyName("due_date")]
    public string DueDate { get; set; } = string.Empty;

    [JsonPropertyName("invoice")]
    public string Invoice { get; set; } = string.Empty;

    [JsonPropertyName("doc_num")]
    public string DocNum { get; set; } = string.Empty;

    [JsonPropertyName("doc_entry")]
    public string DocEntry { get; set; } = string.Empty;

    [JsonPropertyName("purchase_orders")]
    public string PurchaseOrders { get; set; } = string.Empty;

    [JsonPropertyName("fiscalized")]
    public int Fiscalized { get; set; }

    [JsonPropertyName("verification")]
    public string Verification { get; set; } = string.Empty;

    [JsonPropertyName("qrcode")]
    public string QrCode { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public int Status { get; set; }

    [JsonPropertyName("timestamps")]
    public VanSalesLegacyTimestampsDto Timestamps { get; set; } = new();

    [JsonPropertyName("pod")]
    public VanSalesLegacyPodDto Pod { get; set; } = new();

    [JsonPropertyName("order_items")]
    public List<VanSalesLegacyOrderItemDto> OrderItems { get; set; } = new();

    [JsonPropertyName("FiscalizedText")]
    public string FiscalizedText { get; set; } = string.Empty;

    [JsonPropertyName("FiscalizedTextColor")]
    public string FiscalizedTextColor { get; set; } = string.Empty;
}