namespace ShopInventory.Features.DailyIncomingPayments;

/// <summary>A business partner's G/L accounts for its daily incoming payment.</summary>
public sealed record IncomingPaymentGlMappingDto(
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
public sealed record DailyIncomingPaymentSummaryDto(
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
    string? EmailError);

/// <summary>One invoice on a daily incoming payment, and the sale it came from.</summary>
public sealed record DailyIncomingPaymentLineDto(
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
public sealed record DailyIncomingPaymentDetailDto(
    DailyIncomingPaymentSummaryDto Payment,
    List<DailyIncomingPaymentLineDto> Lines);
