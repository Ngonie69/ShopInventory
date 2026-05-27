namespace ShopInventory.DTOs;

public sealed class EnsureInvoiceCrateTransactionRequestDto
{
    public int InvoiceDocNum { get; set; }
    public decimal? ExpectedQuantity { get; set; }
}