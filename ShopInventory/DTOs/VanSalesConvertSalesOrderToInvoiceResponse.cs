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

    [JsonPropertyName("errors")]
    public List<string> Errors { get; set; } = new();
}