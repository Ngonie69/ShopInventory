namespace ShopInventory.DTOs;

public class BulkPodValidationResultDto
{
    public int DocNum { get; set; }
    public int? DocEntry { get; set; }
    public int? SalesOrderDocNum { get; set; }
    public int? SalesOrderDocEntry { get; set; }
    public int? ResolvedInvoiceDocNum { get; set; }
    public int? ResolvedInvoiceDocEntry { get; set; }
    public int LinkedInvoiceCount { get; set; }
    public string? CardCode { get; set; }
    public string? CardName { get; set; }
    public bool Found { get; set; }
    public int ExistingPodCount { get; set; }
    public string? ErrorMessage { get; set; }
}