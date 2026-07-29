namespace ShopInventory.Features.Reports.Queries.GetAccountSalesPaymentReport;

internal sealed record AccountSalesPaymentPaymentApplicationSnapshot(
    int LineNumber,
    string InvoiceReference,
    string InvoiceType,
    decimal AppliedAmount);