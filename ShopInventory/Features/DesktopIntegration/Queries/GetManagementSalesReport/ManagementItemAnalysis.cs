using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetManagementSalesReport;

/// <summary>
/// One item's analysis for the management sales report's drill-down: the same period, scope and channel
/// as the report, measured on that item's lines only. <c>Business</c> confines it to the line of business
/// whose row was opened, so an item shops and vending both sell is taken apart for one of them.
/// </summary>
public sealed record GetManagementItemAnalysisQuery(
    Guid CallerUserId,
    string ItemCode,
    DateTime? FromDate,
    DateTime? ToDate,
    string? WarehouseCode = null,
    string? SourceSystem = null,
    string? CardCode = null,
    string? Business = null
) : IRequest<ErrorOr<ManagementItemAnalysis>>;

public sealed record ManagementItemAnalysis(
    string ItemCode,
    string? ItemDescription,
    int? ItemsGroupCode,
    DateTime FromDate,
    DateTime ToDate,
    DateTime PreviousFromDate,
    DateTime PreviousToDate,
    string? WarehouseCode,
    string? SourceSystem,
    string? CardCode,
    List<ManagementItemCurrencySection> Currencies,
    ManagementMarginStatus Margin);

/// <summary>The item in one currency.</summary>
/// <remarks><list type="table">
/// <item><term>ByDepot</term><description>Where it sold: each shop or depot, keyed by warehouse.</description></item>
/// <item><term>ByPartner</term><description>Who it sold as: each business partner, keyed by CardCode.</description></item>
/// <item><term>ByVendor</term><description>Which vendors took it.</description></item>
/// </list></remarks>
public sealed record ManagementItemCurrencySection(
    string Currency,
    ManagementItemSummary Summary,
    List<ManagementItemDayRow> ByDay,
    List<ManagementItemBreakdownRow> ByDepot,
    List<ManagementItemBreakdownRow> ByPartner,
    List<ManagementItemBreakdownRow> ByVendor,
    List<ManagementItemBreakdownRow> ByChannel,
    List<ManagementItemBreakdownRow> ByOperator);

/// <summary>The item's headline figures. Amounts are line values before VAT.</summary>
public sealed record ManagementItemSummary(
    decimal Quantity,
    decimal NetAmount,
    int SalesCount,
    decimal AverageUnitPrice,
    decimal UnitsPerSale,
    decimal DiscountAmount,
    decimal PreviousQuantity,
    decimal PreviousNetAmount,
    int PreviousSalesCount,
    decimal PreviousAverageUnitPrice,
    decimal? QuantityChangePercent,
    decimal? NetChangePercent,
    decimal? PriceChangePercent,
    decimal? GrossProfit,
    decimal? MarginPercent)
{
    /// <summary>Units of <c>Quantity</c> credited back this period; 0 when none were.</summary>
    public decimal CreditedQuantity { get; init; }

    /// <summary>
    /// The part of <c>NetAmount</c> those units were sold for — before VAT, like <c>NetAmount</c>, and so
    /// not the tax-inclusive figure a sale's credited amount is.
    /// </summary>
    public decimal CreditedNetAmount { get; init; }

    /// <summary><c>Quantity</c> less <see cref="CreditedQuantity"/>.</summary>
    public decimal QuantityAfterCredits => Quantity - CreditedQuantity;

    /// <summary><c>NetAmount</c> less <see cref="CreditedNetAmount"/>: line value after credits, before VAT.</summary>
    public decimal NetAmountAfterCredits => NetAmount - CreditedNetAmount;
}

public sealed record ManagementItemDayRow(
    DateTime Date,
    decimal Quantity,
    decimal NetAmount,
    DateTime ComparedDate,
    decimal ComparedQuantity,
    decimal ComparedNetAmount)
{
    /// <summary>Units of <c>Quantity</c> credited back this period; 0 when none were.</summary>
    public decimal CreditedQuantity { get; init; }

    /// <summary>
    /// The part of <c>NetAmount</c> those units were sold for — before VAT, like <c>NetAmount</c>, and so
    /// not the tax-inclusive figure a sale's credited amount is.
    /// </summary>
    public decimal CreditedNetAmount { get; init; }

    /// <summary><c>Quantity</c> less <see cref="CreditedQuantity"/>.</summary>
    public decimal QuantityAfterCredits => Quantity - CreditedQuantity;

    /// <summary><c>NetAmount</c> less <see cref="CreditedNetAmount"/>: line value after credits, before VAT.</summary>
    public decimal NetAmountAfterCredits => NetAmount - CreditedNetAmount;
}

public sealed record ManagementItemBreakdownRow(
    string Key,
    string Label,
    string? Hint,
    decimal Quantity,
    decimal NetAmount,
    int SalesCount,
    decimal ShareOfNetPercent,
    decimal AverageUnitPrice,
    decimal PreviousQuantity,
    decimal PreviousNetAmount,
    decimal? ChangePercent,
    decimal? GrossProfit,
    decimal? MarginPercent)
{
    /// <summary>Units of <c>Quantity</c> credited back this period; 0 when none were.</summary>
    public decimal CreditedQuantity { get; init; }

    /// <summary>
    /// The part of <c>NetAmount</c> those units were sold for — before VAT, like <c>NetAmount</c>, and so
    /// not the tax-inclusive figure a sale's credited amount is.
    /// </summary>
    public decimal CreditedNetAmount { get; init; }

    /// <summary><c>Quantity</c> less <see cref="CreditedQuantity"/>.</summary>
    public decimal QuantityAfterCredits => Quantity - CreditedQuantity;

    /// <summary><c>NetAmount</c> less <see cref="CreditedNetAmount"/>: line value after credits, before VAT.</summary>
    public decimal NetAmountAfterCredits => NetAmount - CreditedNetAmount;
}
