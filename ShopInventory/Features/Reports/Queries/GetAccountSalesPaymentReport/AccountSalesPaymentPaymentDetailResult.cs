namespace ShopInventory.Features.Reports.Queries.GetAccountSalesPaymentReport;

public sealed class AccountSalesPaymentPaymentDetailResult
{
    public string PeriodLabel { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public DateTime PaymentDateUtc { get; set; }
    public string CardCode { get; set; } = string.Empty;
    public string CardName { get; set; } = string.Empty;
    public string PaymentNumber { get; set; } = string.Empty;
    public string PaymentEntry { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public decimal IncomingPaymentsUsd { get; set; }
    public decimal IncomingPaymentsZig { get; set; }
    public string Reference { get; set; } = string.Empty;
    public int AppliedInvoiceCount { get; set; }
}