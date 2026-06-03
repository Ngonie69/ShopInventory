namespace ShopInventory.Features.Reports.Queries.GetAccountSalesPaymentReport;

public sealed class AccountSalesPaymentAccountResult
{
    public string CardCode { get; set; } = string.Empty;
    public string CardName { get; set; } = string.Empty;
    public int InvoiceCount { get; set; }
    public int PaymentCount { get; set; }
    public decimal TotalQuantitySold { get; set; }
    public decimal TotalSalesUsd { get; set; }
    public decimal TotalSalesZig { get; set; }
    public decimal IncomingPaymentsUsd { get; set; }
    public decimal IncomingPaymentsZig { get; set; }
    public decimal CollectionRatePercentUsd { get; set; }
    public decimal CollectionRatePercentZig { get; set; }
    public List<AccountSalesPaymentItemResult> Items { get; set; } = new();
}