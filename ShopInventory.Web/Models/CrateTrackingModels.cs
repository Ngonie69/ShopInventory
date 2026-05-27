namespace ShopInventory.Web.Models;

public class CrateTransactionDto
{
    public int Id { get; set; }
    public string TransactionType { get; set; } = string.Empty;
    public int? InvoiceDocEntry { get; set; }
    public int? InvoiceDocNum { get; set; }
    public string ShopCardCode { get; set; } = string.Empty;
    public string? ShopName { get; set; }
    public decimal ExpectedQuantity { get; set; }
    public decimal? DriverQuantity { get; set; }
    public decimal? MerchandiserQuantity { get; set; }
    public decimal? VarianceQuantity { get; set; }
    public bool HasDriverPod { get; set; }
    public bool HasMerchandiserPod { get; set; }
    public bool HasDriverPodDocument { get; set; }
    public bool HasMerchandiserPodDocument { get; set; }
    public bool HasGrv { get; set; }
    public int? GrvId { get; set; }
    public string? GrvNumber { get; set; }
    public int SupportingDocumentCount { get; set; }
    public DateTime EffectiveDate { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedByUserName { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Notes { get; set; }
}

public class CratePodSubmissionDto
{
    public int Id { get; set; }
    public int CrateTransactionId { get; set; }
    public int? InvoiceDocNum { get; set; }
    public string ShopCardCode { get; set; } = string.Empty;
    public string? ShopName { get; set; }
    public decimal ExpectedQuantity { get; set; }
    public string SubmissionRole { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public DateTime SubmittedAt { get; set; }
    public string? SubmittedByUserName { get; set; }
    public string? Notes { get; set; }
    public List<DocumentAttachmentDto> Attachments { get; set; } = new();
}

public class CrateGrvDto
{
    public int Id { get; set; }
    public int CrateTransactionId { get; set; }
    public string? GrvNumber { get; set; }
    public int? InvoiceDocNum { get; set; }
    public string ShopCardCode { get; set; } = string.Empty;
    public string? ShopName { get; set; }
    public decimal ExpectedQuantity { get; set; }
    public decimal ActualQuantity { get; set; }
    public decimal VarianceQuantity { get; set; }
    public string Direction { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string? CreatedByUserName { get; set; }
    public List<DocumentAttachmentDto> Attachments { get; set; } = new();
}

public class BulkCratePodValidationRequest
{
    public List<int> InvoiceDocNums { get; set; } = [];
    public string? SubmissionRole { get; set; }
}

public class BulkCratePodValidationResponse
{
    public List<BulkCratePodValidationResult> Results { get; set; } = [];
}

public class BulkCratePodValidationResult
{
    public int InvoiceDocNum { get; set; }
    public int? CrateTransactionId { get; set; }
    public string? ShopCardCode { get; set; }
    public string? ShopName { get; set; }
    public decimal ExpectedQuantity { get; set; }
    public decimal? ExistingQuantity { get; set; }
    public int ExistingAttachmentCount { get; set; }
    public bool HasExistingSubmission { get; set; }
    public string? Status { get; set; }
    public bool Found { get; set; }
    public bool CanUpload { get; set; }
    public string? ErrorMessage { get; set; }
}