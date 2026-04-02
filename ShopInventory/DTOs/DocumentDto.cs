namespace ShopInventory.DTOs;

#region Document Template DTOs

/// <summary>
/// Document template response
/// </summary>
public class DocumentTemplateDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DocumentType { get; set; } = string.Empty;
    public string HtmlContent { get; set; } = string.Empty;
    public string? CssStyles { get; set; }
    public string? HeaderContent { get; set; }
    public string? FooterContent { get; set; }
    public string PaperSize { get; set; } = "A4";
    public string Orientation { get; set; } = "Portrait";
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? CreatedByUserName { get; set; }
}

/// <summary>
/// Create/update document template request
/// </summary>
public class UpsertDocumentTemplateRequest
{
    public string Name { get; set; } = string.Empty;
    public string DocumentType { get; set; } = string.Empty;
    public string HtmlContent { get; set; } = string.Empty;
    public string? CssStyles { get; set; }
    public string? HeaderContent { get; set; }
    public string? FooterContent { get; set; }
    public string PaperSize { get; set; } = "A4";
    public string Orientation { get; set; } = "Portrait";
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Document template list response
/// </summary>
public class DocumentTemplateListResponseDto
{
    public List<DocumentTemplateDto> Templates { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}

#endregion

#region Document Attachment DTOs

/// <summary>
/// Document attachment response
/// </summary>
public class DocumentAttachmentDto
{
    public int Id { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public int EntityId { get; set; }
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

/// <summary>
/// Upload attachment request
/// </summary>
public class UploadAttachmentRequest
{
    public string EntityType { get; set; } = string.Empty;
    public int EntityId { get; set; }
    public string? Description { get; set; }
    public bool IncludeInEmail { get; set; } = true;
}

/// <summary>
/// Document attachment list response
/// </summary>
public class DocumentAttachmentListResponseDto
{
    public List<DocumentAttachmentDto> Attachments { get; set; } = new();
    public int TotalCount { get; set; }
}

/// <summary>
/// POD attachment item with invoice context
/// </summary>
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

/// <summary>
/// Paginated POD attachment list response
/// </summary>
public class PodAttachmentListResponseDto
{
    public List<PodAttachmentItemDto> Items { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public bool HasMore { get; set; }
}

#endregion

#region Document Generation DTOs

/// <summary>
/// Generate document request
/// </summary>
public class GenerateDocumentRequest
{
    public string DocumentType { get; set; } = string.Empty;
    public int EntityId { get; set; }
    public int? TemplateId { get; set; }
    public bool SaveToHistory { get; set; } = true;
}

/// <summary>
/// Email document request
/// </summary>
public class EmailDocumentRequest
{
    public string DocumentType { get; set; } = string.Empty;
    public int EntityId { get; set; }
    public int? TemplateId { get; set; }
    public string RecipientEmail { get; set; } = string.Empty;
    public string? RecipientName { get; set; }
    public string? Subject { get; set; }
    public string? MessageBody { get; set; }
    public List<string>? CcEmails { get; set; }
    public bool IncludeAttachments { get; set; } = true;
    public int? EmailTemplateId { get; set; }
}

/// <summary>
/// Document generation response
/// </summary>
public class GenerateDocumentResponseDto
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? FilePath { get; set; }
    public string? DownloadUrl { get; set; }
    public byte[]? FileContent { get; set; }
    public string? FileName { get; set; }
}

#endregion

#region Document History DTOs

/// <summary>
/// Document history response
/// </summary>
public class DocumentHistoryDto
{
    public int Id { get; set; }
    public string DocumentType { get; set; } = string.Empty;
    public int EntityId { get; set; }
    public string? DocumentNumber { get; set; }
    public string? TemplateName { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? RecipientEmail { get; set; }
    public string? EmailSubject { get; set; }
    public bool? EmailSent { get; set; }
    public string? EmailError { get; set; }
    public DateTime GeneratedAt { get; set; }
    public string? GeneratedByUserName { get; set; }
    public string? DownloadUrl { get; set; }
}

/// <summary>
/// Document history list response
/// </summary>
public class DocumentHistoryListResponseDto
{
    public List<DocumentHistoryDto> History { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}

#endregion

#region Digital Signature DTOs

/// <summary>
/// Digital signature response
/// </summary>
public class DocumentSignatureDto
{
    public int Id { get; set; }
    public string DocumentType { get; set; } = string.Empty;
    public int DocumentId { get; set; }
    public string SignerName { get; set; } = string.Empty;
    public string? SignerEmail { get; set; }
    public string SignerRole { get; set; } = string.Empty;
    public string SignatureData { get; set; } = string.Empty;
    public DateTime SignedAt { get; set; }
    public bool IsVerified { get; set; }
    public string? IpAddress { get; set; }
}

/// <summary>
/// Create signature request
/// </summary>
public class CreateSignatureRequest
{
    public string DocumentType { get; set; } = string.Empty;
    public int DocumentId { get; set; }
    public string SignerName { get; set; } = string.Empty;
    public string? SignerEmail { get; set; }
    public string SignerRole { get; set; } = string.Empty;
    public string SignatureData { get; set; } = string.Empty;
}

/// <summary>
/// Document signature list response
/// </summary>
public class DocumentSignatureListResponseDto
{
    public List<DocumentSignatureDto> Signatures { get; set; } = new();
    public int TotalCount { get; set; }
}

#endregion

#region Email Template DTOs

/// <summary>
/// Email template response
/// </summary>
public class EmailTemplateDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TemplateCode { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string BodyContent { get; set; } = string.Empty;
    public string? CcEmails { get; set; }
    public string? BccEmails { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>
/// Create/update email template request
/// </summary>
public class UpsertEmailTemplateRequest
{
    public string Name { get; set; } = string.Empty;
    public string TemplateCode { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string BodyContent { get; set; } = string.Empty;
    public string? CcEmails { get; set; }
    public string? BccEmails { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Email template list response
/// </summary>
public class EmailTemplateListResponseDto
{
    public List<EmailTemplateDto> Templates { get; set; } = new();
    public int TotalCount { get; set; }
}

#endregion

#region Template Placeholders

/// <summary>
/// Available placeholders for document templates
/// </summary>
public class TemplatePlaceholdersDto
{
    public string DocumentType { get; set; } = string.Empty;
    public List<PlaceholderDto> Placeholders { get; set; } = new();
}

/// <summary>
/// Individual placeholder info
/// </summary>
public class PlaceholderDto
{
    public string Key { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Example { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
}

#endregion

#region POD Upload Status Report DTOs

/// <summary>
/// Single invoice with its POD upload status
/// </summary>
public class PodUploadStatusItemDto
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
    public int PodCount { get; set; }
}

/// <summary>
/// POD upload status report response
/// </summary>
public class PodUploadStatusReportDto
{
    public string FromDate { get; set; } = string.Empty;
    public string ToDate { get; set; } = string.Empty;
    public int TotalInvoices { get; set; }
    public int UploadedCount { get; set; }
    public int PendingCount { get; set; }
    public List<PodUploadStatusItemDto> Items { get; set; } = new();
}

#endregion

#region POD Dashboard DTOs

/// <summary>
/// Personal POD dashboard stats for the current user
/// </summary>
public class PodDashboardDto
{
    public string Username { get; set; } = string.Empty;
    public int UploadsToday { get; set; }
    public int UploadsThisWeek { get; set; }
    public int UploadsThisMonth { get; set; }
    public int TotalUploads { get; set; }
    public long TotalFileSizeBytes { get; set; }
    public string TotalFileSizeFormatted { get; set; } = string.Empty;
    public int UniqueInvoicesCovered { get; set; }
    public List<PodRecentUploadDto> RecentUploads { get; set; } = new();
    public List<PodDailyCountDto> DailyUploads { get; set; } = new();
}

/// <summary>
/// Recent POD upload for dashboard
/// </summary>
public class PodRecentUploadDto
{
    public int Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public int InvoiceDocEntry { get; set; }
    public int InvoiceDocNum { get; set; }
    public string? CardName { get; set; }
    public string FileSizeFormatted { get; set; } = string.Empty;
    public DateTime UploadedAt { get; set; }
}

/// <summary>
/// Daily upload count for chart
/// </summary>
public class PodDailyCountDto
{
    public string Date { get; set; } = string.Empty;
    public int Count { get; set; }
}

#endregion
