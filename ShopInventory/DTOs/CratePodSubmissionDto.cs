namespace ShopInventory.DTOs;

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