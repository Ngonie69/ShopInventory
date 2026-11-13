namespace ShopInventory.Features.VendingCashUps;

/// <summary>
/// One cashier's day laid out for counting: what the sales say, and what has been counted so far.
/// </summary>
/// <remarks>
/// Answered for a day that has no cash-up yet too, with <see cref="CashUpId"/> null and every counted
/// figure null, so the capture form opens on the expected figures. A null counted figure is "not
/// counted", never zero, and has no variance.
/// </remarks>
public sealed record VendingCashUpSheetDto(
    int? CashUpId,
    int Revision,
    string Status,
    Guid CashierUserId,
    string CashierUsername,
    string CashierName,
    string? BusinessPartnerCode,
    string? DepotName,
    string WarehouseCode,
    DateTime BusinessDate,
    IReadOnlyList<VendingCashUpCurrencyDto> Currencies,
    IReadOnlyList<VendingCashUpTenderDto> Tenders,
    string? VarianceReason,
    string? Note,
    bool Balanced,
    bool NeedsReason,
    bool SalesChangedSinceCount,
    bool SavedByCaller,
    string? QueryNote,
    string? QueriedByName,
    DateTime? QueriedAt,
    string? CapturedByName,
    DateTime? CapturedAt,
    string? UpdatedByName,
    DateTime? UpdatedAt,
    string? ApprovedByName,
    DateTime? ApprovedAt);

/// <remarks>
/// <para><c>ExpectedCash</c>: The cash sales as they stand now.</para>
/// <para><c>CountedExpectedCash</c>: The cash sales the saved count was made against; null when not counted.</para>
/// <para><c>Variance</c>: Counted less float and cash sales; null when not counted.</para>
/// <para><c>ToBank</c>: The takings in the count (counted less float); null when not counted.</para>
/// </remarks>
public sealed record VendingCashUpCurrencyDto(
    string Currency,
    decimal ExpectedCash,
    int ExpectedCashSales,
    decimal? CountedExpectedCash,
    decimal? OpeningFloat,
    decimal? CountedCash,
    decimal? Banked,
    string? DepositReference,
    decimal? Variance,
    decimal? ToBank,
    IReadOnlyList<VendingCashUpDenominationDto> Denominations);

public sealed record VendingCashUpTenderDto(
    string Currency,
    string Tender,
    decimal Expected,
    int ExpectedSales,
    decimal? CountedExpected,
    decimal? Declared,
    decimal? Variance);

public sealed record VendingCashUpDenominationDto(decimal Value, int Quantity);

/// <summary>Every cashier-day in a range that had vending sales or a cash-up, and the totals over them.</summary>
public sealed record VendingReconciliationDto(
    DateTime FromDate,
    DateTime ToDate,
    IReadOnlyList<VendingReconciliationRowDto> Rows,
    IReadOnlyList<VendingReconciliationCurrencyTotalDto> Totals,
    VendingReconciliationCountsDto Counts);

/// <summary>One cashier's day at one depot warehouse.</summary>
/// <remarks>
/// <para><c>Status</c>: "NotCashedUp", or one of <see cref="VendingCashUpStatuses"/>.</para>
/// </remarks>
public sealed record VendingReconciliationRowDto(
    int? CashUpId,
    int Revision,
    string Status,
    DateTime BusinessDate,
    Guid CashierUserId,
    string CashierUsername,
    string CashierName,
    string? BusinessPartnerCode,
    string? DepotName,
    string WarehouseCode,
    IReadOnlyList<VendingReconciliationCurrencyDto> Currencies,
    bool Balanced,
    bool NeedsReason,
    bool SalesChangedSinceCount,
    string? VarianceReason,
    string? QueryNote,
    string? CapturedByName,
    DateTime? CapturedAt,
    string? ApprovedByName,
    DateTime? ApprovedAt);

/// <summary>
/// One currency of a cashier's day. <see cref="Invoiced"/> is every sale in the currency, all tenders;
/// <see cref="Accounted"/> is the cash counted less the float, plus the non-cash slips. Both counted
/// figures and the variance are null on a day with no count.
/// </summary>
public sealed record VendingReconciliationCurrencyDto(
    string Currency,
    decimal Invoiced,
    int Sales,
    decimal ExpectedCash,
    decimal? OpeningFloat,
    decimal? CountedCash,
    decimal? Accounted,
    decimal? Banked,
    decimal? Variance);

/// <remarks>
/// <para><c>OpenShortfall</c>: Shortfalls on counts not yet approved, as a positive amount.</para>
/// </remarks>
public sealed record VendingReconciliationCurrencyTotalDto(
    string Currency,
    decimal Invoiced,
    decimal InvoicedOnCountedDays,
    decimal Accounted,
    decimal Banked,
    decimal NetVariance,
    decimal OpenShortfall);

public sealed record VendingReconciliationCountsDto(
    int CashierDays,
    int NotCashedUp,
    int AwaitingApproval,
    int Queried,
    int Approved,
    int BalancedOnFirstCount,
    int NeedsReason);
