using System.Text.Json.Serialization;

namespace ShopInventory.DTOs;

/// <summary>
/// The answer to "did the sale I posted under this <c>van_order</c> land?" — <c>GET api/vansales/sale/{vanOrder}</c>.
/// </summary>
/// <remarks>
/// <para>A van loses replies as a matter of course, and a lost reply to <c>POST order</c> usually means the
/// sale landed: the receipt was signed, the customer may be holding it, and the handset cannot tell. This is
/// read off the sale row the post writes, so it answers the moment the receipt is signed — invoice history
/// reads SAP, and a sale the queue has not posted yet is not there.</para>
///
/// <para><b>Not found is a 200 with <see cref="Found"/> false, never a 404.</b> The handset reads a 404 as a
/// server that predates this route, and tells the rep it could not check. Every other field is then null or
/// false. A sale outside the caller's scope answers exactly the same way.</para>
///
/// <para>Bare, not wrapped in <see cref="VanSalesEnvelope{T}"/>: nothing legacy consumes it.</para>
/// </remarks>
public sealed class VanSalesSaleLookupResponse
{
    [JsonPropertyName("found")]
    public bool Found { get; set; }

    /// <summary>The reference asked about, echoed whether or not a sale was found under it.</summary>
    [JsonPropertyName("van_order")]
    public string VanOrder { get; set; } = string.Empty;

    /// <summary>
    /// <c>INV10427</c> — the number the slip is headed with and the console shows. The same derivation as the
    /// POST's <c>sale_number</c>: see <see cref="Common.Sales.DesktopSaleNumber"/>.
    /// </summary>
    [JsonPropertyName("sale_number")]
    public string? SaleNumber { get; set; }

    /// <summary>
    /// A receipt exists and can be printed: the sale fiscalised successfully and carries a verification code.
    /// False for a sale still pending, refused, or waiting on a person to check the device.
    /// </summary>
    [JsonPropertyName("fiscalised")]
    public bool Fiscalised { get; set; }

    /// <summary>
    /// The stored status by name: <c>Pending</c>, <c>Success</c>, <c>Failed</c> or <c>Skipped</c>.
    /// </summary>
    [JsonPropertyName("fiscal_status")]
    public string? FiscalStatus { get; set; }

    [JsonPropertyName("verification_code")]
    public string? VerificationCode { get; set; }

    [JsonPropertyName("qr_code")]
    public string? QrCode { get; set; }

    [JsonPropertyName("fiscal_day")]
    public string? FiscalDay { get; set; }

    [JsonPropertyName("device_serial")]
    public string? DeviceSerial { get; set; }

    [JsonPropertyName("receipt_global_no")]
    public int? ReceiptGlobalNo { get; set; }

    /// <summary>Null until the invoice queue has posted the sale to SAP.</summary>
    [JsonPropertyName("sap_doc_num")]
    public int? SapDocNum { get; set; }

    /// <summary>Null until the invoice queue has posted the sale to SAP.</summary>
    [JsonPropertyName("sap_doc_entry")]
    public int? SapDocEntry { get; set; }

    /// <summary>The account the invoice bills — on a route-customer van, the van's own business partner.</summary>
    [JsonPropertyName("card_code")]
    public string? CardCode { get; set; }

    /// <summary>Who bought: the shop's name on a route-customer van.</summary>
    [JsonPropertyName("card_name")]
    public string? CardName { get; set; }

    /// <summary>What the customer pays, tax included.</summary>
    [JsonPropertyName("total")]
    public decimal? Total { get; set; }

    [JsonPropertyName("currency")]
    public string? Currency { get; set; }

    /// <summary>When the server took the sale, in UTC.</summary>
    [JsonPropertyName("created_at_utc")]
    public DateTime? CreatedAtUtc { get; set; }
}
