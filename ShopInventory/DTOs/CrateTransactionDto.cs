namespace ShopInventory.DTOs;

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