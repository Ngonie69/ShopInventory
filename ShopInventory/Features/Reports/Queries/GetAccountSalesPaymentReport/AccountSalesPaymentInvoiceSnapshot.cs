namespace ShopInventory.Features.Reports.Queries.GetAccountSalesPaymentReport;

internal sealed record AccountSalesPaymentInvoiceSnapshot(
    int DocumentKey,
    string Source,
    string DocumentNumber,
    string DocumentEntry,
    string CardCode,
    string CardName,
    DateTime DocumentDateUtc,
    string Currency,
    string Status,
    decimal DocumentTotal,
    decimal TotalSalesUsd,
    decimal TotalSalesZig,
    IReadOnlyList<AccountSalesPaymentItemSnapshot> Items);