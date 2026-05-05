namespace ShopInventory.Web.Models;

public sealed class BatchStatusHistoryAuditRow
{
    public int AuditLogId { get; set; }
    public DateTime Timestamp { get; set; }
    public string Username { get; set; } = string.Empty;
    public string UserRole { get; set; } = string.Empty;
    public string? EntityId { get; set; }
    public string? Details { get; set; }
    public bool IsSuccess { get; set; }
    public string? ErrorMessage { get; set; }
}