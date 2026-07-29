namespace ShopInventory.Features.Reports.Queries.GetAccountSalesPaymentReport;

internal sealed record AccountSalesPaymentItemSnapshot(
    int LineNumber,
    string ItemCode,
    string ItemName,
    decimal QuantitySold,
    decimal LineAmount,
    decimal SalesUsd,
    decimal SalesZig);