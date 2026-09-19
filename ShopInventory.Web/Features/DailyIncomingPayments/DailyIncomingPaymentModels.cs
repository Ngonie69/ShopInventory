namespace ShopInventory.Web.Features.DailyIncomingPayments;

// Hand mirrors of ShopInventory/Features/DailyIncomingPayments/DailyIncomingPaymentDtos.cs. Nullability
// matches the API's field for field: a mismatch deserialises to nothing and the page reads as empty.

/// <summary>A business partner's G/L accounts for its daily incoming payment.</summary>
public sealed record IncomingPaymentGlMapping(
    string CardCode,
    string? CardName,
    string CashAccount,
    string ElectronicAccount,
    string Run,
    string? NotifyEmails,
    bool NeedsReview,
    bool IsActive,
    DateTime UpdatedAtUtc,
    string? UpdatedBy);

/// <summary>One daily incoming payment, as the list shows it.</summary>
public sealed record DailyIncomingPaymentSummary(
    int Id,
    string Reference,
    string CardCode,
    string? CardName,
    DateTime PaymentDate,
    string Status,
    decimal CashSum,
    decimal ElectronicSum,
    string? CashAccount,
    string? TransferAccount,
    int? SapDocNum,
    DateTime? PostedAtUtc,
    int InvoiceCount,
    string? LastError,
    DateTime? EmailSentAtUtc,
    string? EmailError)
{
    public decimal Total => CashSum + ElectronicSum;
}

/// <summary>One invoice on a daily incoming payment, and the sale it came from.</summary>
public sealed record DailyIncomingPaymentLine(
    int InvoiceDocEntry,
    int? InvoiceDocNum,
    string Source,
    int? DesktopSaleId,
    string? SaleReference,
    DateTime? SaleDate,
    string? PaymentMethod,
    decimal CashAmount,
    decimal ElectronicAmount);

/// <summary>A daily incoming payment with its invoices.</summary>
public sealed record DailyIncomingPaymentDetail(
    DailyIncomingPaymentSummary Payment,
    List<DailyIncomingPaymentLine> Lines);

/// <summary>The body of <c>PUT api/daily-incoming-payments/gl-mappings/{cardCode}</c>.</summary>
public sealed record SaveIncomingPaymentGlMappingBody(
    string? CardName,
    string CashAccount,
    string ElectronicAccount,
    string Run,
    string? NotifyEmails,
    bool IsActive);
