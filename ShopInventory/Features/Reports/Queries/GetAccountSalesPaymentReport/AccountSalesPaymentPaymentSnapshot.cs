namespace ShopInventory.Features.Reports.Queries.GetAccountSalesPaymentReport;

internal sealed record AccountSalesPaymentPaymentSnapshot(
    string Source,
    string PaymentNumber,
    string PaymentEntry,
    string Status,
    string Currency,
    string Reference,
    string CardCode,
    string CardName,
    DateTime PaymentDateUtc,
    decimal TotalAmount,
    decimal IncomingPaymentsUsd,
    decimal IncomingPaymentsZig,
    IReadOnlyList<AccountSalesPaymentPaymentApplicationSnapshot> Applications);