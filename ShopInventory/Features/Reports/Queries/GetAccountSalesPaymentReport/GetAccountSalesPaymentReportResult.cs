namespace ShopInventory.Features.Reports.Queries.GetAccountSalesPaymentReport;

public sealed class GetAccountSalesPaymentReportResult
{
    public DateTime GeneratedAtUtc { get; set; }
    public DateTime FromDateUtc { get; set; }
    public DateTime ToDateUtc { get; set; }
    public AccountSalesPaymentGrouping Grouping { get; set; }
    public List<string> RequestedAccountCodes { get; set; } = new();
    public List<string> Sources { get; set; } = new();
    public AccountSalesPaymentSummaryResult Summary { get; set; } = new();
    public List<AccountSalesPaymentAccountResult> AccountTotals { get; set; } = new();
    public List<AccountSalesPaymentPeriodResult> Periods { get; set; } = new();
    public List<AccountSalesPaymentInvoiceDetailResult> InvoiceDetails { get; set; } = new();
    public List<AccountSalesPaymentPaymentDetailResult> PaymentDetails { get; set; } = new();
    public List<AccountSalesPaymentPaymentApplicationResult> PaymentApplications { get; set; } = new();
}