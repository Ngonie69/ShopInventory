namespace ShopInventory.DTOs;

public class ConvertSalesOrderToInvoiceResponseDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int SalesOrderId { get; set; }
    public string? SalesOrderNumber { get; set; }
    public string ExternalReference { get; set; } = string.Empty;
    public string? ReservationId { get; set; }
    public int? QueueId { get; set; }
    public string Status { get; set; } = string.Empty;
    public int EstimatedProcessingSeconds { get; set; }
    public string? StatusUrl { get; set; }
    public List<string> Errors { get; set; } = new();
}
