using System.Text.Json.Serialization;

namespace ShopInventory.DTOs;

/// <summary>
/// The answer to <c>GET api/vansales/sales-order/{vanOrder}</c>: whether the sales order a handset sent
/// under that <c>van_order</c> landed.
/// </summary>
/// <remarks>
/// The sales-order twin of <see cref="VanSalesSaleLookupResponse"/>, asked by a handset whose
/// <c>POST sales-order</c> lost its reply. Always a 200 for an active account; <c>found: false</c> covers both
/// an order that is not there and one the caller may not see. Every other field is null when not found.
/// </remarks>
public sealed class VanSalesSalesOrderLookupResponse
{
    [JsonPropertyName("found")]
    public bool Found { get; set; }

    /// <summary>The reference asked about, echoed whether or not an order was found under it.</summary>
    [JsonPropertyName("van_order")]
    public string VanOrder { get; set; } = string.Empty;

    /// <summary>The local sales order id — what conversion and history name the order by.</summary>
    [JsonPropertyName("order_id")]
    public int? OrderId { get; set; }

    /// <summary>The order number the office and the handset's order history show.</summary>
    [JsonPropertyName("order_number")]
    public string? OrderNumber { get; set; }

    /// <summary>The order's status by name: <c>Pending</c>, <c>Approved</c>, <c>Fulfilled</c> and so on.</summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    /// <summary>Null until the order has been posted to SAP.</summary>
    [JsonPropertyName("sap_doc_num")]
    public int? SapDocNum { get; set; }

    [JsonPropertyName("card_code")]
    public string? CardCode { get; set; }

    [JsonPropertyName("card_name")]
    public string? CardName { get; set; }

    [JsonPropertyName("total")]
    public decimal? Total { get; set; }

    [JsonPropertyName("currency")]
    public string? Currency { get; set; }

    [JsonPropertyName("created_at_utc")]
    public DateTime? CreatedAtUtc { get; set; }
}
