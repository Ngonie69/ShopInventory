using System.Text.Json.Serialization;
using ShopInventory.Common.Sales;

namespace ShopInventory.DTOs;

/// <summary>
/// A van sale made with signal: it posts to SAP inside the request and the customer waits for the reply.
/// </summary>
/// <remarks>
/// Since the fleet gained fiscal devices this also carries a signed ZIMRA receipt, in exactly the fields
/// <see cref="VanSalesOfflineSaleRequest"/> uses and under exactly the same JSON names. That is not
/// tidiness: a handset owns one device and one hash chain, and it stamps every sale it makes off that one
/// chain whatever the network was doing. Two receipt formats would mean two signing routines on the
/// handset, and the moment they diverged some of a device's receipts would be unarchivable — which stops
/// the whole device, because the platform accepts receipt N+1 only once it holds N.
///
/// <para>
/// All of the receipt fields are optional. A handset built before the signing release sends none of them
/// and its sale is still accepted, flagged as unstamped, and reported until
/// <c>Fiscalisation:RequireStampedVanSales</c> is switched on.
/// </para>
/// </remarks>
public class VanSalesOrderRequest : IVanSalesSignedReceipt
{
    [JsonPropertyName("customer")]
    public int Customer { get; set; }

    [JsonPropertyName("customer_code")]
    public string? CustomerCode { get; set; }

    [JsonPropertyName("ref")]
    public string Reference { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = string.Empty;

    [JsonPropertyName("amount_paid")]
    public double AmountPaid { get; set; }

    [JsonPropertyName("change")]
    public double Change { get; set; }

    [JsonPropertyName("due_date")]
    public string DueDate { get; set; } = string.Empty;

    [JsonPropertyName("latitude")]
    public string Latitude { get; set; } = string.Empty;

    [JsonPropertyName("auto_post")]
    public int AutoPost { get; set; }

    [JsonPropertyName("van_order")]
    public string VanOrder { get; set; } = string.Empty;

    [JsonPropertyName("longitude")]
    public string Longitude { get; set; } = string.Empty;

    /// <summary>
    /// How the customer paid, as a brand — "Cash", "Ecocash", "Innbucks". Optional: handsets built
    /// before the payment step existed send nothing, and the sale is then reported as untendered
    /// rather than assumed to be cash.
    /// </summary>
    [JsonPropertyName("payment_method")]
    public string? PaymentMethod { get; set; }

    [JsonPropertyName("sales_order")]
    public string SalesOrder { get; set; } = string.Empty;

    [JsonPropertyName("sales_order_id")]
    public int? SalesOrderId { get; set; }

    [JsonPropertyName("items")]
    public List<VanSalesOrderItemRequest> Items { get; set; } = new();

    // --- Already fiscalised on the handset; reported here so ZIMRA can still be given the receipt ---
    //
    // Field for field the same set VanSalesOfflineSaleRequest carries, and for the same reason: the
    // fiscalisation platform re-derives the signed payload from what is forwarded and refuses anything
    // that does not hash to the signature below. Nothing here is rounded, re-derived or defaulted.

    [JsonPropertyName("fiscal_device_id")]
    public string? FiscalDeviceId { get; set; }

    [JsonPropertyName("fiscal_day_no")]
    public int? FiscalDayNo { get; set; }

    [JsonPropertyName("receipt_global_no")]
    public int? ReceiptGlobalNo { get; set; }

    [JsonPropertyName("receipt_counter")]
    public int? ReceiptCounter { get; set; }

    [JsonPropertyName("verification_code")]
    public string? VerificationCode { get; set; }

    [JsonPropertyName("qr_code")]
    public string? QrCode { get; set; }

    /// <summary>
    /// The instant the receipt was signed at, in the van's local wall clock at second precision.
    ///
    /// Distinct from <see cref="DueDate"/> and from the trading day the invoice posts against: this is
    /// part of the signed payload, and a one-second difference invalidates the signature.
    /// </summary>
    [JsonPropertyName("receipt_date")]
    public DateTime? ReceiptDate { get; set; }

    /// <summary>
    /// When the handset's fiscal day was opened, local wall clock. The platform never opened this day
    /// itself, so it has no other way to learn it.
    /// </summary>
    [JsonPropertyName("fiscal_day_opened_at")]
    public DateTime? FiscalDayOpenedAt { get; set; }

    /// <summary>
    /// The hash this receipt was chained onto, or null for the first receipt of a fiscal day. Sent
    /// explicitly rather than inferred from the receipt before it, so a divergence is reported as the
    /// chain break it is instead of surfacing as an unexplained signature failure.
    /// </summary>
    [JsonPropertyName("previous_receipt_hash")]
    public string? PreviousReceiptHash { get; set; }

    /// <summary>Base64 SHA-256 of the canonical payload the handset signed.</summary>
    [JsonPropertyName("device_signature_hash")]
    public string? DeviceSignatureHash { get; set; }

    /// <summary>Base64 RSA-PKCS1-SHA256 signature over that same payload.</summary>
    [JsonPropertyName("device_signature_value")]
    public string? DeviceSignatureValue { get; set; }

    /// <summary>Whether this sale carries everything the platform needs to accept the receipt.</summary>
    public bool HasSignedReceipt() => VanSalesSignedReceipt.HasSignedReceipt(this);

    /// <summary>
    /// Whether the handset took a number off its device's chain for this sale — which is also the test
    /// for whether the server may fiscalise it. See
    /// <see cref="VanSalesSignedReceipt.ClaimsReceiptSequence"/>.
    /// </summary>
    public bool ClaimsReceiptSequence() => VanSalesSignedReceipt.ClaimsReceiptSequence(this);
}