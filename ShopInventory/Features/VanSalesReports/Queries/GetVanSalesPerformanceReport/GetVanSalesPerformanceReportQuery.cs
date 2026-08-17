using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VanSalesReports.Queries.GetVanSalesPerformanceReport;

/// <summary>
/// What the vans sold over a period, cut by route, rep, item and time.
/// </summary>
/// <remarks>
/// <c>FromDate</c> and <c>ToDate</c> are inclusive CAT trading days, matching the compliance report —
/// a van's day belongs to the van, not to the server's zone.
///
/// <c>RouteCode</c> excludes sales whose rep opened no departure record that day, for the same reason
/// the compliance report does: nothing on an unrouted sale says which route it belonged to, so a route
/// filter must not be satisfied by one.
/// </remarks>
public sealed record GetVanSalesPerformanceReportQuery(
    DateTime FromDate,
    DateTime ToDate,
    Guid? UserId = null,
    string? RouteCode = null,
    int TopItems = 50
) : IRequest<ErrorOr<VanSalesPerformanceReportResult>>;

public sealed record VanSalesPerformanceReportResult(
    DateTime FromDate,
    DateTime ToDate,
    VanSalesPerformanceSummaryResult Summary,
    List<VanSalesTerritoryResult> Territories,
    List<VanSalesRouteResult> Routes,
    List<VanSalesRepResult> Reps,
    List<VanSalesItemResult> Items,
    List<VanSalesLapsedItemResult> LapsedItems,
    VanSalesTrendResult Trend,
    List<VanSalesItemPriceResult> ItemPrices,
    List<VanSalesDropSizeResult> DropSizes,
    VanSalesCoverageResult Coverage
);

// ── Money and quantity ──────────────────────────────────────────────────────────
//
// There is no scalar money field anywhere on this tree, and no scalar quantity. Both are always a
// list, because both are meaningless when collapsed: USD and ZiG are different money, and a van's
// lines carry no unit of measure at all today, so a quantity total across items would be adding
// crates to kilos to eaches. Making the caller pick a bucket is the point.

/// <summary>
/// One currency's takings at document grain. <c>Gross</c> is the sum of <c>TotalAmount</c>, so it
/// includes VAT — offline sales carry a VAT figure and online ones do not, so no net is derivable
/// and none is offered.
/// </summary>
public sealed record VanSalesMoneyResult(
    string Currency,
    int DocumentCount,
    int DropCount,
    decimal Gross)
{
    /// <summary>Null rather than zero: a currency with no documents has no average, it has no data.</summary>
    public decimal? AverageDocumentValue =>
        DocumentCount > 0 ? decimal.Round(Gross / DocumentCount, 2) : null;

    /// <summary>
    /// The drop size. A drop is one shop on one day in one currency, so two invoices written at the
    /// same counter are one drop — which is what a field manager means by the word.
    /// </summary>
    public decimal? AverageDropSize =>
        DropCount > 0 ? decimal.Round(Gross / DropCount, 2) : null;
}

/// <summary>
/// One currency's takings at line grain. Deliberately a different type from
/// <see cref="VanSalesMoneyResult"/>: document totals and line totals are two measures, not one, and
/// a row that mixed them would be reconcilable to nothing.
/// </summary>
public sealed record VanSalesLineMoneyResult(
    string Currency,
    int LineCount,
    decimal Gross);

/// <summary>
/// How much moved, in one unit of measure. <c>UoMCode</c> is null for every van sale written to date
/// — neither ingest path sets it — so expect a single "unit not recorded" bucket and do not read that
/// as an error.
/// </summary>
public sealed record VanSalesQuantityResult(
    string? UoMCode,
    decimal Quantity,
    int LineCount);

// ── Route and territory ─────────────────────────────────────────────────────────

/// <summary>
/// A territory's trade, folded up from its routes.
///
/// Emitted rather than left to the caller because the counts do not fold: two routes each worked by
/// the same rep are one rep in the territory, not two, and a page adding the route rows would say two.
/// </summary>
public sealed record VanSalesTerritoryResult(
    string? Territory,
    int RouteCount,
    int RepCount,
    int TradingDayCount,
    int ProductiveCalls,
    int CustomerCount,
    List<VanSalesMoneyResult> TotalsByCurrency);

/// <summary>
/// One route's trade over the period.
/// </summary>
/// <remarks>
/// The route is the one stamped on the rep's departure record for that day, never the rep's current
/// assignment — <c>VanRouteDayEntity</c> snapshots it precisely so that moving a rep does not silently
/// re-label their history.
///
/// Four states share this record and none of them may be confused for another:
/// a routed day; a day opened before a route was named (<c>HasRouteDay</c> true, <c>RouteCode</c>
/// null); a routed day whose route has no territory (<c>Territory</c> null); and a sale whose rep
/// never opened a day at all (<c>HasRouteDay</c> false, everything null). The last sorts last and is
/// never shown among real routes, because it is not a route — it is a gap, and its size is the
/// finding.
/// </remarks>
public sealed record VanSalesRouteResult(
    bool HasRouteDay,
    string? RouteCode,
    string? RouteName,
    string? Territory,
    int RepCount,
    int TradingDayCount,
    int? PlannedCalls,
    int? Calls,
    int ProductiveCalls,
    int CustomerCount,
    int? KilometresTravelled,
    List<VanSalesMoneyResult> TotalsByCurrency)
{
    /// <summary>
    /// Calls made over calls planned. Null when no departure record supplied a plan — nothing was
    /// planned, so nothing can have been missed, and 0% would read as total failure.
    /// </summary>
    public double? CallComplianceRate =>
        PlannedCalls is > 0 && Calls is { } calls ? (double)calls / PlannedCalls.Value : null;

    /// <summary>
    /// The calls that bought, over the calls made. Can exceed 1.0 where sales exist with no recorded
    /// visit, and is deliberately not clamped — that discrepancy is itself worth seeing.
    /// </summary>
    public double? ProductiveCallRate =>
        Calls is > 0 ? (double)ProductiveCalls / Calls.Value : null;
}

// ── Reps ────────────────────────────────────────────────────────────────────────

/// <summary>
/// One rep's period, which is the league table a manager actually reads.
/// </summary>
/// <remarks>
/// <c>Calls</c> and <c>OutletsVisited</c> are different measures and both are carried: a shop called
/// on twice in a week is two calls and one outlet, and which one a question means is not guessable.
///
/// <c>NewOutlets</c> and <c>NewOutletsWhoBought</c> are likewise separate. Capturing a shop and
/// converting it are different achievements, and folding them would hide reps who register outlets
/// that never trade.
/// </remarks>
public sealed record VanSalesRepResult(
    Guid UserId,
    string Username,
    string? FullName,
    List<string> Routes,
    int TradingDayCount,
    int? Calls,
    int? OutletsVisited,
    int ProductiveCalls,
    int CustomerCount,
    int NewOutlets,
    int NewOutletsWhoBought,
    int ItemCount,
    int? KilometresTravelled,
    List<VanSalesMoneyResult> TotalsByCurrency)
{
    public string DisplayName => string.IsNullOrWhiteSpace(FullName) ? Username : FullName;

    /// <summary>
    /// Null, not zero, when the rep has no van visit records at all. A rep with sales and no visits
    /// is not a 0% striker — he is unmeasurable, and the unmeasurability is the finding.
    /// </summary>
    public double? StrikeRate =>
        Calls is > 0 ? (double)ProductiveCalls / Calls.Value : null;

    /// <summary>Calls per day worked, over days that actually traded.</summary>
    public double? CallsPerDay =>
        TradingDayCount > 0 && Calls is { } calls ? (double)calls / TradingDayCount : null;
}

// ── Items ───────────────────────────────────────────────────────────────────────

/// <summary>
/// One item's movement. Ranked on reach — lines and buying customers — rather than on value or
/// quantity, because value cannot be compared across currencies and quantity cannot be compared
/// across units.
/// </summary>
public sealed record VanSalesItemResult(
    int Rank,
    string ItemCode,
    string? ItemDescription,
    int LineCount,
    int DocumentCount,
    int CustomerCount,
    int RepCount,
    int TradingDayCount,
    DateTime FirstSoldOn,
    DateTime LastSoldOn,
    List<VanSalesQuantityResult> QuantitiesByUoM,
    List<VanSalesLineMoneyResult> TotalsByCurrency);

/// <summary>
/// An item that sold in the preceding window of equal length and has not sold in this one.
/// </summary>
/// <remarks>
/// Measured against what actually sold before, not against a catalogue: the API has no item
/// catalogue — its <c>Products</c> table is never populated — so "stocked but never sold" is a
/// question only the portal can ask, and answering it here would mean inventing the denominator.
/// </remarks>
public sealed record VanSalesLapsedItemResult(
    string ItemCode,
    string? ItemDescription,
    DateTime LastSoldOn,
    int DaysSinceLastSale,
    int PriorLineCount,
    int PriorCustomerCount,
    List<VanSalesLineMoneyResult> PriorTotalsByCurrency);

// ── Trend ───────────────────────────────────────────────────────────────────────

public sealed record VanSalesTrendResult(
    List<VanSalesTrendPointResult> Daily,
    List<VanSalesSeasonPointResult> DayOfWeek,
    List<VanSalesSeasonPointResult> Monthly);

/// <summary>
/// One calendar day. Days with no trade are present with an empty <c>TotalsByCurrency</c>, not
/// absent: a silent day is a finding, and dropping it makes the spacing of the curve lie.
/// </summary>
public sealed record VanSalesTrendPointResult(
    DateTime TradingDate,
    DayOfWeek DayOfWeek,
    int RepsTrading,
    int ProductiveCalls,
    int DocumentCount,
    List<VanSalesMoneyResult> TotalsByCurrency);

/// <summary>
/// A repeating slice of the period — one weekday, or one calendar month.
/// </summary>
/// <remarks>
/// <c>CalendarDayCount</c> and <c>ActiveDayCount</c> are both carried because any average has to
/// divide by the first. Dividing by the second makes a weekday nobody works look like an average one.
///
/// <c>IsPartial</c> marks a month the window only half covers, so a short first month is not read as
/// a collapse in trade.
/// </remarks>
public sealed record VanSalesSeasonPointResult(
    string Label,
    int? Year,
    int? Month,
    DayOfWeek? DayOfWeek,
    bool IsPartial,
    int CalendarDayCount,
    int ActiveDayCount,
    int DocumentCount,
    int ProductiveCalls,
    List<VanSalesMoneyResult> TotalsByCurrency);

// ── Price realisation ───────────────────────────────────────────────────────────

/// <summary>
/// What one item actually fetched, in one currency and one unit.
/// </summary>
/// <remarks>
/// This is peer-relative and must be presented as such. It is not measured against a list price,
/// because there is no trustworthy local one: the API holds no populated item catalogue, and the
/// portal's cached price is a single price-list snapshot in which list 1 is mostly zero.
///
/// It replaces the discount report that was originally wanted. Neither van ingest path ever writes
/// <c>DiscountPercent</c>, so every van line in both tables is 0.00 — a discount panel would have
/// been a page of zeros presented as perfect price discipline. Under-pricing still shows here,
/// because it shows in the price actually achieved.
///
/// The achieved price is <c>LineTotal / Quantity</c>, not <c>UnitPrice</c>. The two disagree once
/// per-line rounding lands, and what the customer paid is the line total.
/// </remarks>
public sealed record VanSalesItemPriceResult(
    string ItemCode,
    string? ItemDescription,
    string Currency,
    string? UoMCode,
    int LineCount,
    decimal Quantity,
    decimal Gross,
    decimal WeightedAveragePrice,
    decimal MinUnitPrice,
    decimal MaxUnitPrice,
    List<VanSalesRepPriceResult> Reps)
{
    /// <summary>
    /// How wide the spread is, against the item's own average. Null when the average is zero — a
    /// promotional or written-off line has no meaningful spread, and dividing would read as infinite
    /// rather than as inapplicable.
    /// </summary>
    public decimal? PriceSpreadPercent =>
        WeightedAveragePrice == 0
            ? null
            : decimal.Round((MaxUnitPrice - MinUnitPrice) / WeightedAveragePrice * 100m, 2);
}

public sealed record VanSalesRepPriceResult(
    Guid UserId,
    string Username,
    string? FullName,
    int LineCount,
    decimal Quantity,
    decimal Gross,
    decimal WeightedAveragePrice,
    decimal? VarianceFromItemPercent)
{
    public string DisplayName => string.IsNullOrWhiteSpace(FullName) ? Username : FullName;
}

// ── Drop size ───────────────────────────────────────────────────────────────────

/// <summary>
/// The shape of a period's drops in one currency, not just their average.
/// </summary>
/// <remarks>
/// The distribution is the point: a route whose median drop is half its mean is carrying a handful of
/// large accounts and a long tail of calls that barely pay for the stop, and an average alone says
/// none of that.
/// </remarks>
public sealed record VanSalesDropSizeResult(
    string Currency,
    int DropCount,
    decimal Total,
    decimal Minimum,
    decimal P25,
    decimal Median,
    decimal P75,
    decimal Maximum,
    decimal Mean,
    List<VanSalesDropSizeBucketResult> Buckets);

/// <summary>
/// One band of the distribution. Bands are fixed rather than derived from the data's own quantiles,
/// so two periods can be compared — a band that moves with the data compares nothing.
/// </summary>
public sealed record VanSalesDropSizeBucketResult(
    string Label,
    decimal LowerBound,
    decimal? UpperBound,
    int DropCount,
    decimal Total)
{
    public double? SharePercent { get; init; }
}

// ── Summary and coverage ────────────────────────────────────────────────────────

public sealed record VanSalesPerformanceSummaryResult(
    int RepCount,
    int RouteCount,
    int TerritoryCount,
    int TradingDayCount,
    int DocumentCount,
    int? Calls,
    int ProductiveCalls,
    int CustomerCount,
    int ItemCount,
    int NewOutlets,
    int? KilometresTravelled,
    List<VanSalesMoneyResult> TotalsByCurrency)
{
    public double? StrikeRate =>
        Calls is > 0 ? (double)ProductiveCalls / Calls.Value : null;
}

/// <summary>
/// What this report could not see, counted rather than hidden.
/// </summary>
/// <remarks>
/// Every field here is a silent under-report waiting to happen: a sale with no shop on it, a rep-day
/// with no departure record, a currency that was assumed rather than stated. None of them throw and
/// none of them show up as an empty page — they just make a number smaller than the truth. Carrying
/// the counts to the surface lets a reader tell a quiet week from a broken feed.
/// </remarks>
public sealed record VanSalesCoverageResult(
    int SaleCount,
    int LineCount,
    int SalesWithoutRouteCustomer,
    int SalesWithoutRouteDay,
    int RepDaysWithoutRouteDay,
    int SalesWithAssumedCurrency,
    int SalesWithoutPaymentMethod,
    int LinesWithoutUoM,
    int LinesWithZeroQuantity,
    int RepsWithoutVisitData,
    int ReferencesInBothSources)
{
    /// <summary>True when nothing above needs saying, so the page can hide the strip entirely.</summary>
    public bool IsClean =>
        SalesWithoutRouteCustomer == 0
        && SalesWithoutRouteDay == 0
        && SalesWithAssumedCurrency == 0
        && LinesWithZeroQuantity == 0
        && RepsWithoutVisitData == 0
        && ReferencesInBothSources == 0;
}
