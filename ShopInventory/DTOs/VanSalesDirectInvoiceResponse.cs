using System.Text.Json.Serialization;

namespace ShopInventory.DTOs;

public class VanSalesDirectInvoiceResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("external_reference")]
    public string ExternalReference { get; set; } = string.Empty;

    [JsonPropertyName("reservation_id")]
    public string ReservationId { get; set; } = string.Empty;

    [JsonPropertyName("sap_doc_entry")]
    public int? SapDocEntry { get; set; }

    [JsonPropertyName("sap_doc_num")]
    public int? SapDocNum { get; set; }

    [JsonPropertyName("was_queued")]
    public bool WasQueued { get; set; }

    [JsonPropertyName("queue_id")]
    public int? QueueId { get; set; }

    [JsonPropertyName("queue_status")]
    public string? QueueStatus { get; set; }

    [JsonPropertyName("queue_external_reference")]
    public string? QueueExternalReference { get; set; }

    [JsonPropertyName("estimated_processing_seconds")]
    public int? EstimatedProcessingSeconds { get; set; }

    [JsonPropertyName("status_url")]
    public string? StatusUrl { get; set; }

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