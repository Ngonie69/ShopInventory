namespace ShopInventory.DTOs;

public sealed class MerchandiserPurchaseOrderReportAttachmentDto
{
    public int AttachmentId { get; init; }
    public string FileName { get; init; } = string.Empty;
    public string? MimeType { get; init; }
    public long FileSizeBytes { get; init; }
    public string? Description { get; init; }
    public DateTime UploadedAtUtc { get; init; }
    public string? UploadedByUsername { get; init; }
}