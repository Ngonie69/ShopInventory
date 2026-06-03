namespace ShopInventory.Features.Reports.Queries.GetAccountSalesPaymentReport;

public sealed class AccountSalesPaymentItemResult
{
    public string ItemCode { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public int InvoiceCount { get; set; }
    public decimal TotalQuantitySold { get; set; }
    public decimal TotalSalesUsd { get; set; }
    public decimal TotalSalesZig { get; set; }
}