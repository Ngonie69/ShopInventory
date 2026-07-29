namespace ShopInventory.Features.Reports.Queries.GetAccountSalesPaymentReport;

public sealed class AccountSalesPaymentInvoiceDetailResult
{
    public string PeriodLabel { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public DateTime DocumentDateUtc { get; set; }
    public string CardCode { get; set; } = string.Empty;
    public string CardName { get; set; } = string.Empty;
    public string DocumentNumber { get; set; } = string.Empty;
    public string DocumentEntry { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
    public decimal DocumentTotal { get; set; }
    public int LineNumber { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public decimal QuantitySold { get; set; }
    public decimal LineAmount { get; set; }
    public decimal SalesUsd { get; set; }
    public decimal SalesZig { get; set; }
}