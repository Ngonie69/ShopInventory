namespace ShopInventory.DTOs;

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