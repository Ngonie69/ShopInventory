namespace ShopInventory.DTOs;

public class QueuedInvoiceResponseDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string ExternalReference { get; set; } = string.Empty;
    public string ReservationId { get; set; } = string.Empty;
    public int QueueId { get; set; }
    public string Status { get; set; } = string.Empty;
    public int EstimatedProcessingSeconds { get; set; }
    public string? StatusUrl { get; set; }
}
