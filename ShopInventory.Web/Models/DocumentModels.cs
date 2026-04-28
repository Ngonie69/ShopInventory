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

public class PodAttachmentItemDto
{
    public int Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string? MimeType { get; set; }
    public long FileSizeBytes { get; set; }
    public string FileSizeFormatted { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime UploadedAt { get; set; }
    public string? UploadedByUserName { get; set; }
    public string DownloadUrl { get; set; } = string.Empty;
    public int InvoiceDocEntry { get; set; }
    public int InvoiceDocNum { get; set; }
    public string? CardCode { get; set; }
    public string? CardName { get; set; }
}

public class PodAttachmentListResponse
{
    public List<PodAttachmentItemDto> Items { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public bool HasMore { get; set; }
}

public class PodUploadStatusItem
{
    public int DocEntry { get; set; }
    public int DocNum { get; set; }
    public string? DocDate { get; set; }
    public string? CardCode { get; set; }
    public string? CardName { get; set; }
    public decimal DocTotal { get; set; }
    public string? DocCurrency { get; set; }
    public bool HasPod { get; set; }
    public DateTime? PodUploadedAt { get; set; }
    public string? PodUploadedBy { get; set; }
    public List<PodUploadUserSummary> PodUploadedByUsers { get; set; } = new();
    public int PodCount { get; set; }
}

public class PodUploadUserSummary
{
    public string Username { get; set; } = string.Empty;
    public int FileCount { get; set; }
    public DateTime? LatestUploadedAt { get; set; }
}

public class PodUploadStatusReport
{
    public string FromDate { get; set; } = string.Empty;
    public string ToDate { get; set; } = string.Empty;
    public int TotalInvoices { get; set; }
    public int UploadedCount { get; set; }
    public int PendingCount { get; set; }
    public List<PodUploadStatusItem> Items { get; set; } = new();
}

public class PodDashboardModel
{
    public string Username { get; set; } = string.Empty;
    public int UploadsToday { get; set; }
    public int UploadsThisWeek { get; set; }
    public int UploadsThisMonth { get; set; }
    public int TotalUploads { get; set; }
    public long TotalFileSizeBytes { get; set; }
    public string TotalFileSizeFormatted { get; set; } = string.Empty;
    public int UniqueInvoicesCovered { get; set; }
    public List<PodRecentUpload> RecentUploads { get; set; } = new();
    public List<PodDailyCount> DailyUploads { get; set; } = new();
}

public class PodRecentUpload
{
    public int Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public int InvoiceDocEntry { get; set; }
    public int InvoiceDocNum { get; set; }
    public string? CardName { get; set; }
    public string FileSizeFormatted { get; set; } = string.Empty;
    public DateTime UploadedAt { get; set; }
}

public class PodDailyCount
{
    public string Date { get; set; } = string.Empty;
    public int Count { get; set; }
}
