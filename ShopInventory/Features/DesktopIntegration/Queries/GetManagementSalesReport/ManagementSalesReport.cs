namespace ShopInventory.Features.DesktopIntegration.Queries.GetManagementSalesReport;

/// <summary>The management sales report.</summary>
/// <remarks><list type="table">
/// <item><term>PreviousFromDate</term><description>First day of the comparison period: the same number of days, just before.</description></item>
/// <item><term>Currencies</term><description>One section per currency. Currencies are never added together.</description></item>
/// <item><term>Health</term><description>Posting and fiscalisation for every sale in the period, whatever its currency.</description></item>
/// <item><term>Margin</term><description>Whether gross margin could be read from SAP, and why not when it could not.</description></item>
/// <item><term>Partners</term><description>Every business partner that sold under the caller's scope in either period, ignoring the channel and partner filters — what the page's partner filter chooses from.</description></item>
/// </list></remarks>
public sealed record ManagementSalesReport(
    DateTime FromDate,
    DateTime ToDate,
    DateTime PreviousFromDate,
    DateTime PreviousToDate,
    string? WarehouseCode,
    string? SourceSystem,
    string? CardCode,
    DateTime GeneratedAtUtc,
    List<ManagementCurrencySection> Currencies,
    ManagementSalesHealth Health,
    ManagementMarginStatus Margin,
    List<ManagementPartner> Partners);

/// <summary>A business partner sales were made as, with the warehouses those sales left from.</summary>
public sealed record ManagementPartner(string CardCode, string CardName, List<string> Warehouses);

/// <summary>Everything the report says about one currency.</summary>
/// <remarks><list type="table">
/// <item><term>Summary</term><description>The headline figures, now and in the previous period.</description></item>
/// <item><term>ByDay</term><description>Each day of the period beside the matching day of the previous one.</description></item>
/// <item><term>ByChannel</term><description>Shop till, vending, van sales.</description></item>
/// <item><term>ByDepot</term><description>Each shop or depot, keyed by the warehouse its stock leaves.</description></item>
/// <item><term>ByPartner</term><description>Each business partner the sales were made as, keyed by CardCode. One warehouse can serve several partners, so this is not ByDepot renamed.</description></item>
/// <item><term>ByVendor</term><description>Each vendor served. Sales to walk-in customers carry no vendor and are not here.</description></item>
/// <item><term>LapsedVendors</term><description>Vendors who bought in the previous period and not at all in this one.</description></item>
/// <item><term>ByItem</term><description>Every item sold, not a top slice — the tail is where a range decision is made.</description></item>
/// <item><term>ByItemGroup</term><description>The items rolled up by SAP item group. Items the product master does not know are one "ungrouped" row.</description></item>
/// <item><term>ItemDepotMatrix</term><description>What each item sold at each shop or depot this period, one cell per pair that traded.</description></item>
/// <item><term>ItemPartnerMatrix</term><description>What each item sold as each business partner this period, one cell per pair that traded.</description></item>
/// </list></remarks>
public sealed record ManagementCurrencySection(
    string Currency,
    ManagementSummary Summary,
    List<ManagementDayRow> ByDay,
    List<ManagementBreakdownRow> ByChannel,
    List<ManagementBreakdownRow> ByDepot,
    List<ManagementBreakdownRow> ByPartner,
    List<ManagementBreakdownRow> ByCostCentre,
    List<ManagementBreakdownRow> ByOperator,
    List<ManagementBreakdownRow> ByPaymentMethod,
    List<ManagementBreakdownRow> ByVendor,
    List<ManagementLapsedVendorRow> LapsedVendors,
    List<ManagementItemRow> ByItem,
    List<ManagementItemGroupRow> ByItemGroup,
    List<ManagementItemDepotCell> ItemDepotMatrix,
    List<ManagementItemPartnerCell> ItemPartnerMatrix);

/// <summary>One SAP item group's sales, set against the previous period.</summary>
/// <remarks><list type="table">
/// <item><term>ItemsGroupCode</term><description>SAP's group number; null for items the product master has no group for.</description></item>
/// <item><term>ItemCount</term><description>Items in the group that sold in either period.</description></item>
/// </list></remarks>
public sealed record ManagementItemGroupRow(
    int? ItemsGroupCode,
    int ItemCount,
    decimal Quantity,
    decimal NetAmount,
    decimal ShareOfNetPercent,
    decimal PreviousQuantity,
    decimal PreviousNetAmount,
    decimal? ChangePercent,
    decimal? GrossProfit,
    decimal? MarginPercent);

/// <summary>One item at one shop or depot, this period.</summary>
public sealed record ManagementItemDepotCell(string ItemCode, string WarehouseCode, decimal Quantity, decimal NetAmount);

/// <summary>One item sold as one business partner, this period.</summary>
public sealed record ManagementItemPartnerCell(string ItemCode, string CardCode, decimal Quantity, decimal NetAmount);

/// <summary>Headline figures for the period and the one before it.</summary>
/// <remarks><list type="table">
/// <item><term>TotalAmount</term><description>Takings, VAT included.</description></item>
/// <item><term>NetAmount</term><description>Takings before VAT.</description></item>
/// <item><term>GrossProfit</term><description>SAP's gross profit on the sales it holds cost for; null when none could be read.</description></item>
/// <item><term>MarginPercent</term><description>Gross profit over the revenue SAP costed, not over all takings.</description></item>
/// <item><term>CostedNetAmount</term><description>Revenue SAP returned a cost for — the base the margin is measured on.</description></item>
/// <item><term>CostedSalesCount</term><description>Sales whose invoice SAP returned a cost for.</description></item>
/// </list></remarks>
public sealed record ManagementSummary(
    int SalesCount,
    decimal TotalAmount,
    decimal VatAmount,
    decimal NetAmount,
    decimal AverageSale,
    decimal QuantitySold,
    int DaysTraded,
    int VendorsServed,
    int PreviousSalesCount,
    decimal PreviousTotalAmount,
    decimal PreviousNetAmount,
    decimal PreviousAverageSale,
    int PreviousVendorsServed,
    decimal? TotalChangePercent,
    decimal? SalesCountChangePercent,
    decimal? AverageSaleChangePercent,
    decimal? GrossProfit,
    decimal? MarginPercent,
    decimal CostedNetAmount,
    int CostedSalesCount);

/// <remarks><list type="table">
/// <item><term>ComparedDate</term><description>The day of the previous period in the same position.</description></item>
/// </list></remarks>
public sealed record ManagementDayRow(
    DateTime Date,
    int SalesCount,
    decimal TotalAmount,
    DateTime ComparedDate,
    int ComparedSalesCount,
    decimal ComparedTotalAmount);

/// <summary>One row of a breakdown, with its previous-period figures and margin.</summary>
/// <remarks><list type="table">
/// <item><term>Hint</term><description>A second line for the label — a code under a name.</description></item>
/// <item><term>ChangePercent</term><description>Null when there was nothing to compare with, rather than an infinite rise.</description></item>
/// <item><term>GrossProfit</term><description>Null where SAP holds no cost for the row, or the dimension cannot carry one.</description></item>
/// </list></remarks>
public sealed record ManagementBreakdownRow(
    string Key,
    string Label,
    string? Hint,
    int SalesCount,
    decimal TotalAmount,
    decimal NetAmount,
    decimal ShareOfValuePercent,
    int PreviousSalesCount,
    decimal PreviousTotalAmount,
    decimal? ChangePercent,
    decimal? GrossProfit,
    decimal? MarginPercent);

public sealed record ManagementLapsedVendorRow(
    string Key,
    string Label,
    string? Hint,
    int PreviousSalesCount,
    decimal PreviousTotalAmount,
    DateTime LastSaleDate);

/// <remarks><list type="table">
/// <item><term>NetAmount</term><description>Line value before VAT.</description></item>
/// <item><term>AverageUnitPrice</term><description>What one unit actually sold for, after discount and before VAT: value over quantity.</description></item>
/// <item><term>UnitsPerSale</term><description>Quantity over the sales that carried the item — how much a customer takes at once.</description></item>
/// <item><term>DiscountAmount</term><description>List value less what was charged, before VAT: what the discounts on this item gave away.</description></item>
/// </list></remarks>
public sealed record ManagementItemRow(
    string ItemCode,
    string? ItemDescription,
    int? ItemsGroupCode,
    decimal Quantity,
    decimal NetAmount,
    int SalesCount,
    decimal ShareOfNetPercent,
    decimal PreviousQuantity,
    decimal PreviousNetAmount,
    decimal? ChangePercent,
    decimal? QuantityChangePercent,
    decimal AverageUnitPrice,
    decimal PreviousAverageUnitPrice,
    decimal? PriceChangePercent,
    decimal UnitsPerSale,
    decimal DiscountAmount,
    decimal? GrossProfit,
    decimal? MarginPercent);

/// <summary>A count of sales and what they are worth, per currency.</summary>
public sealed record ManagementHealthBucket(int SalesCount, List<ManagementCurrencyAmount> Value);

public sealed record ManagementCurrencyAmount(string Currency, decimal Amount);

/// <summary>
/// Whether the period's sales have reached ZIMRA and SAP.
/// </summary>
/// <remarks><list type="table">
/// <item><term>PostingSalesCount</term><description>Sales the desktop posting job is responsible for — till and vending. Van sales reach SAP through the van workflow and are left out of the posting buckets rather than counted as never posted.</description></item>
/// <item><term>PostingWaiting</term><description>Not posted and not yet tried — usually waiting on fiscalisation.</description></item>
/// <item><term>PostingFailing</term><description>Not posted after at least one attempt.</description></item>
/// <item><term>OldestUnpostedDate</term><description>The business day of the oldest sale SAP does not have yet.</description></item>
/// <item><term>LatestPostingError</term><description>The most recent refusal from SAP, so the page can say why.</description></item>
/// </list></remarks>
public sealed record ManagementSalesHealth(
    int SalesCount,
    ManagementHealthBucket FiscalSucceeded,
    ManagementHealthBucket FiscalPending,
    ManagementHealthBucket FiscalFailed,
    ManagementHealthBucket FiscalSkipped,
    ManagementHealthBucket NeedsReconciliation,
    int PostingSalesCount,
    ManagementHealthBucket Posted,
    ManagementHealthBucket PostingWaiting,
    ManagementHealthBucket PostingFailing,
    ManagementHealthBucket PaymentFailed,
    DateTime? OldestUnpostedDate,
    string? LatestPostingError);

/// <summary>Where the margin figures came from, or why there are none.</summary>
/// <remarks><list type="table">
/// <item><term>Available</term><description>False when SAP could not be read; every margin field is then null.</description></item>
/// <item><term>Detail</term><description>What the figures are, or what went wrong, in words for the page.</description></item>
/// </list></remarks>
public sealed record ManagementMarginStatus(bool Available, string Detail);
