namespace ShopInventory.DTOs;

public sealed class EnsureInvoiceCrateTransactionResponseDto
{
    public int Id { get; set; }
    public string TransactionType { get; set; } = string.Empty;
    public int? InvoiceDocEntry { get; set; }
    public int? InvoiceDocNum { get; set; }
    public string ShopCardCode { get; set; } = string.Empty;
    public string? ShopName { get; set; }
    public decimal ExpectedQuantity { get; set; }
}