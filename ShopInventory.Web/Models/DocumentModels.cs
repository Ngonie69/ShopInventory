namespace ShopInventory.Web.Models;

public class DocumentAttachmentDto
{
    public int Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string? MimeType { get; set; }
    public long FileSizeBytes { get; set; }
    public string FileSizeFormatted { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IncludeInEmail { get; set; }
    public DateTime UploadedAt { get; set; }
    public string? UploadedByUserName { get; set; }
    public string DownloadUrl { get; set; } = string.Empty;
}

public class DocumentAttachmentListResponse
{
    public List<DocumentAttachmentDto> Attachments { get; set; } = new();
    public int TotalCount { get; set; }
}
