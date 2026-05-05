namespace ShopInventory.Web.Models;

public sealed class BatchStatusHistoryItem
{
    public int AuditLogId { get; set; }
    public int BatchEntryId { get; set; }
    public string BatchNumber { get; set; } = string.Empty;
    public string ItemCode { get; set; } = string.Empty;
    public string? WarehouseCode { get; set; }
    public string? FromStatus { get; set; }
    public string? ToStatus { get; set; }
    public string Username { get; set; } = string.Empty;
    public string UserRole { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public bool IsSuccess { get; set; }
    public string? ErrorMessage { get; set; }
}