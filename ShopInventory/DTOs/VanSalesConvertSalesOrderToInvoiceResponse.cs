using System.Text.Json.Serialization;

namespace ShopInventory.DTOs;

public class VanSalesConvertSalesOrderToInvoiceResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("sales_order_id")]
    public int SalesOrderId { get; set; }

    [JsonPropertyName("sales_order_number")]
    public string? SalesOrderNumber { get; set; }

    [JsonPropertyName("external_reference")]
    public string ExternalReference { get; set; } = string.Empty;

    [JsonPropertyName("reservation_id")]
    public string? ReservationId { get; set; }

    [JsonPropertyName("queue_id")]
    public int? QueueId { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("estimated_processing_seconds")]
    public int EstimatedProcessingSeconds { get; set; }

    [JsonPropertyName("status_url")]
    public string? StatusUrl { get; set; }

    /// <summary>
    /// The receipt the office signed before it answered — the same fields, under the same names, as
    /// <see cref="VanSalesDirectInvoiceResponse"/>, so the handset reads a converted invoice the way it reads
    /// a direct sale and prints the slip off the receipt rather than waiting for SAP.
    /// </summary>
    [JsonPropertyName("sale_number")]
    public string? SaleNumber { get; set; }

    [JsonPropertyName("was_queued")]
    public bool WasQueued { get; set; }

    [JsonPropertyName("sap_doc_entry")]
    public int? SapDocEntry { get; set; }

    [JsonPropertyName("sap_doc_num")]
    public int? SapDocNum { get; set; }

    [JsonPropertyName("verification_code")]
    public string? VerificationCode { get; set; }

    [JsonPropertyName("qr_code")]
    public string? QrCode { get; set; }

    [JsonPropertyName("fiscal_day")]
    public string? FiscalDay { get; set; }

    [JsonPropertyName("receipt_global_no")]
    public string? ReceiptGlobalNo { get; set; }

    [JsonPropertyName("device_serial")]
    public string? DeviceSerial { get; set; }

    [JsonPropertyName("errors")]
    public List<string> Errors { get; set; } = new();
}