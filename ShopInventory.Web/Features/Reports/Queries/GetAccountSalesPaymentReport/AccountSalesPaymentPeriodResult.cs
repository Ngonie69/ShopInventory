namespace ShopInventory.Web.Features.Reports.Queries.GetAccountSalesPaymentReport;

public sealed class AccountSalesPaymentPeriodResult
{
    public string Label { get; set; } = string.Empty;
    public DateTime PeriodStartUtc { get; set; }
    public DateTime PeriodEndUtc { get; set; }
    public int InvoiceCount { get; set; }
    public int PaymentCount { get; set; }
    public decimal TotalQuantitySold { get; set; }
    public decimal TotalSalesUsd { get; set; }
    public decimal TotalSalesZig { get; set; }
    public decimal IncomingPaymentsUsd { get; set; }
    public decimal IncomingPaymentsZig { get; set; }
    public List<AccountSalesPaymentAccountResult> Accounts { get; set; } = new();
}