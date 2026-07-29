namespace ShopInventory.Web.Features.Reports.Queries.GetAccountSalesPaymentReport;

public sealed class AccountSalesPaymentSummaryResult
{
    public int RequestedAccountCount { get; set; }
    public int ActiveAccountCount { get; set; }
    public int TotalPeriods { get; set; }
    public int TotalInvoices { get; set; }
    public int TotalPayments { get; set; }
    public decimal TotalQuantitySold { get; set; }
    public decimal TotalSalesUsd { get; set; }
    public decimal TotalSalesZig { get; set; }
    public decimal TotalIncomingPaymentsUsd { get; set; }
    public decimal TotalIncomingPaymentsZig { get; set; }
    public decimal CollectionRatePercentUsd { get; set; }
    public decimal CollectionRatePercentZig { get; set; }
}