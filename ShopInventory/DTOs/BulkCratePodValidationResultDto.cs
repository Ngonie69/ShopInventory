namespace ShopInventory.DTOs;

public class BulkCratePodValidationResultDto
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