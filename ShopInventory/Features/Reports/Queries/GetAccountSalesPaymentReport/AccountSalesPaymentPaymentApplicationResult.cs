namespace ShopInventory.Features.Reports.Queries.GetAccountSalesPaymentReport;

public sealed class AccountSalesPaymentPaymentApplicationResult
{
    public string PeriodLabel { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public DateTime PaymentDateUtc { get; set; }
    public string CardCode { get; set; } = string.Empty;
    public string CardName { get; set; } = string.Empty;
    public string PaymentNumber { get; set; } = string.Empty;
    public string PaymentEntry { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string AppliedInvoiceReference { get; set; } = string.Empty;
    public string InvoiceType { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
    public decimal AppliedAmount { get; set; }
}