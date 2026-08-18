using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VanSalesReports.Queries.GetVanMarginReport;

/// <summary>
/// What sold off the vans, per item and per van, and how much of it SAP is in a position to cost.
/// </summary>
/// <remarks>
/// This is the reporting plan's A10 — gross margin by SKU and route. Revenue is local and covers
/// every van sale; cost comes from the invoice lines SAP posted, so it covers only the sales that
/// reached SAP. Both figures are reported, and so is the gap between them.
///
/// <b>Margin is stated per currency and only where the two sides share one.</b> B1 stamps a line's
/// cost in the company's local currency while the line's revenue is in the document's, and this
/// company bills in two. Subtracting one from the other produces a figure that looks entirely
/// plausible and is arithmetic between two kinds of money, so the report will not do it: a currency
/// whose revenue is not the cost currency gets revenue, no cost and no margin, and says so.
///
/// <b>The costable share is the figure to read first.</b> A margin can only ever exist for a sale
/// SAP knows about. The offline half of van trading is held locally until a posting job drains it,
/// and where that job is off the costable share is a fraction of the whole — so a margin here
/// describes that fraction and not the fleet. The share is on the summary, on every item and on
/// every van, precisely so nobody reads the margin without it.
///
/// The join is (warehouse, item). The line-grain warehouse is populated on both halves of the union,
/// which is why the report keys on it rather than on the van's business partner, whose code is the
/// van's own account on every sale and identifies nothing about the goods.
///
/// There is deliberately no "lines with no warehouse" figure. Both line entities mark the column
/// required, and the offline ingest refuses an entire batch from a rep with no assigned warehouse
/// rather than accepting one — so the count could never be anything but zero, and a counter that
/// cannot move reads as a check that passed rather than one that cannot fail.
/// </remarks>
public sealed record GetVanMarginReportQuery(
    DateTime FromDate,
    DateTime ToDate,
    Guid? UserId = null,
    string? WarehouseCode = null,
    bool IncludeCost = true
) : IRequest<ErrorOr<VanMarginReportResult>>;

public sealed record VanMarginReportResult(
    DateTime FromDate,
    DateTime ToDate,
    VanMarginSummaryResult Summary,
    List<VanMarginItemResult> Items,
    List<VanMarginVanResult> Vans,
    List<VanMarginRouteResult> Routes,
    VanMarginQualityResult Quality
);

// ── Summary ─────────────────────────────────────────────────────────────────────

public sealed record VanMarginSummaryResult(
    int ItemCount,
    int VanCount,
    int LineCount,
    int PostedLineCount,
    List<VanSalesLineMoneyResult> RevenueByCurrency,
    List<VanSalesLineMoneyResult> CostableRevenueByCurrency,
    List<VanSalesQuantityResult> QuantitiesByUoM,
    string? CostCurrency,
    List<VanMarginMoneyResult> MarginByCurrency)
{
    /// <summary>
    /// The share of lines whose sale reached SAP, and which a cost could therefore be found for.
    /// Null when nothing sold — a period with no lines has no costable share, and 0% would read as
    /// an estate whose posting has completely failed rather than one that had a quiet week.
    /// </summary>
    public double? CostableLineShare =>
        LineCount > 0 ? (double)PostedLineCount / LineCount : null;

    /// <summary>
    /// Whether any margin could be stated at all. A field rather than something a reader infers from
    /// a column of dashes, because a reader who infers it wrongly infers that the vans sell at cost.
    /// </summary>
    public bool MarginAvailable => MarginByCurrency.Count > 0;
}

/// <summary>
/// One currency's margin: the revenue that could be costed, the cost, and the difference.
/// </summary>
/// <remarks>
/// Only ever produced for a currency matching the cost currency, so the subtraction is between two
/// figures in the same money. A currency that does not match gets no row here at all rather than a
/// row with a null margin — an absent row cannot be mistaken for a margin of nothing.
/// </remarks>
public sealed record VanMarginMoneyResult(
    string Currency,
    int LineCount,
    decimal Revenue,
    decimal Cost)
{
    public decimal Margin => Revenue - Cost;

    /// <summary>
    /// Margin as a share of revenue. Null when nothing sold in this currency. Legitimately negative
    /// where an item sold below cost, which is a finding rather than an error.
    /// </summary>
    public double? MarginRate => Revenue != 0 ? (double)(Margin / Revenue) : null;
}

// ── Per item ────────────────────────────────────────────────────────────────────

/// <summary>
/// One item across every van in the period.
/// </summary>
/// <remarks>
/// Quantity is per unit of measure and never totalled across units. Van lines carry no unit at all —
/// neither ingest path writes one — so in practice this is a single "not recorded" bucket, and that
/// is honest rather than broken.
/// </remarks>
public sealed record VanMarginItemResult(
    string ItemCode,
    string? ItemDescription,
    int LineCount,
    int PostedLineCount,
    int VanCount,
    List<VanSalesLineMoneyResult> RevenueByCurrency,
    List<VanSalesLineMoneyResult> CostableRevenueByCurrency,
    List<VanSalesQuantityResult> QuantitiesByUoM,
    decimal? UnitCost,
    string? CostCurrency,
    List<VanMarginMoneyResult> MarginByCurrency)
{
    public double? CostableLineShare =>
        LineCount > 0 ? (double)PostedLineCount / LineCount : null;

    /// <summary>
    /// Whether a usable cost was found for this item. Null unit cost and not zero: a zero would
    /// report an item that costs nothing to sell, and B1 does leave the column at zero on a line
    /// whose item has no valuation yet.
    /// </summary>
    public bool HasCost => UnitCost is > 0;
}

// ── Per van ─────────────────────────────────────────────────────────────────────

/// <summary>
/// One van's warehouse across every item in the period.
/// </summary>
/// <remarks>
/// Keyed on the warehouse rather than on the business partner. <c>CardCode</c> on a van sale is the
/// van's own SAP account, which is the same on every sale a route-customer van makes and says
/// nothing about the goods; the warehouse is what an invoice line carries and is therefore the only
/// key a cost can be joined on.
/// </remarks>
public sealed record VanMarginVanResult(
    string WarehouseCode,
    string? Username,
    string? FullName,
    int ItemCount,
    int LineCount,
    int PostedLineCount,
    List<VanSalesLineMoneyResult> RevenueByCurrency,
    List<VanSalesLineMoneyResult> CostableRevenueByCurrency)
{
    public string DisplayName =>
        string.IsNullOrWhiteSpace(FullName)
            ? string.IsNullOrWhiteSpace(Username) ? WarehouseCode : Username
            : FullName;

    public double? CostableLineShare =>
        LineCount > 0 ? (double)PostedLineCount / LineCount : null;
}

// ── Per route ───────────────────────────────────────────────────────────────────

/// <summary>
/// One selling route: what it earned, what that cost, and how far it went to do it.
/// </summary>
/// <remarks>
/// <b>This is contribution, not profitability, and the difference matters.</b> The reporting plan
/// asks for route profitability so a route can be kept, merged or killed — but nothing in this
/// system records what a route costs to run. There is no fuel, no driver time, no vehicle standing
/// cost and no depot overhead anywhere in either database. What is here is gross margin on the goods
/// plus the distance covered earning it, which is the input to that decision rather than the answer.
/// Calling it profit would invite somebody to close a route on a number that has never seen its
/// largest cost.
///
/// A route is taken from the departure record for the rep-day a line was sold on, never from the
/// rep's current route assignment: that would silently re-label every historical sale the moment a
/// rep moved route. Sales whose rep never opened a day carry nothing saying which route they were
/// made on and are gathered into their own row rather than dropped.
/// </remarks>
public sealed record VanMarginRouteResult(
    string RouteCode,
    string? RouteName,
    string? Territory,
    int VanCount,
    int ItemCount,
    int LineCount,
    int PostedLineCount,
    int? Kilometres,
    List<VanSalesLineMoneyResult> RevenueByCurrency,
    List<VanSalesLineMoneyResult> CostableRevenueByCurrency,
    List<VanMarginMoneyResult> MarginByCurrency)
{
    public string DisplayName => string.IsNullOrWhiteSpace(RouteName) ? RouteCode : RouteName;

    public double? CostableLineShare =>
        LineCount > 0 ? (double)PostedLineCount / LineCount : null;

    /// <summary>
    /// Margin earned per kilometre driven, per currency. Null where either side is missing — an
    /// odometer nobody read is not a route that stood still, and a route with no margin has none to
    /// spread over its distance.
    /// </summary>
    /// <remarks>
    /// A ratio and never a total, so it crosses no currency: each row divides one currency's margin
    /// by the same distance. Two routes billing in different currencies still cannot be ranked
    /// against each other on it, which is why it sits beside the margin rather than replacing it.
    /// </remarks>
    public List<VanMarginPerDistanceResult> MarginPerKilometre =>
        Kilometres is > 0
            ? MarginByCurrency
                .Select(margin => new VanMarginPerDistanceResult(
                    margin.Currency,
                    decimal.Round(margin.Margin / Kilometres.Value, 4)))
                .ToList()
            : [];
}

/// <summary>One currency's margin per kilometre. A ratio, so it is never summed with anything.</summary>
public sealed record VanMarginPerDistanceResult(string Currency, decimal MarginPerKilometre);

// ── Quality ─────────────────────────────────────────────────────────────────────

public sealed record VanMarginQualityResult(
    int LineCount,
    int PostedLineCount,
    int ItemsWithNoDescription,
    int ItemsWithoutCost,
    int ItemCount,
    int RouteCount,
    int LinesWithNoRoute,
    string? CostCurrency,
    bool CostAttempted,
    List<string> CurrenciesWithoutMatchingCost,
    bool PostingJobEnabled)
{
    public int UnpostedLineCount => LineCount - PostedLineCount;

    /// <summary>
    /// Whether a cost was established at all. False means every margin figure on the report is
    /// absent — not zero.
    /// </summary>
    public bool CostAvailable => CostCurrency is not null;

    /// <summary>
    /// Clean means the margin describes the whole period: everything posted, everything costed, and
    /// no currency left out. That is a high bar and most periods will not meet it, which is the
    /// point — a margin over two thirds of the trading is a different number from a margin over all
    /// of it, and only this says which one is on the screen.
    /// </summary>
    public bool IsClean =>
        CostAvailable
        && UnpostedLineCount == 0
        && ItemsWithoutCost == 0
        && CurrenciesWithoutMatchingCost.Count == 0
        && ItemsWithNoDescription == 0;

    public IEnumerable<string> Caveats
    {
        get
        {
            if (!CostAttempted)
            {
                yield return
                    "Costs were not fetched for this run, so no margin is stated. Everything below is "
                    + "revenue and quantity.";
            }
            else if (!CostAvailable)
            {
                yield return
                    "No cost could be read from SAP for this period, so no margin is stated. Either "
                    + "SAP was unreachable or the company currency could not be established — and "
                    + "without knowing what a cost is denominated in, subtracting it from revenue "
                    + "would be arithmetic between two kinds of money.";
            }

            if (!PostingJobEnabled)
            {
                yield return
                    "The van sales posting job is switched off in this environment, so offline van "
                    + "sales never reach SAP however long they wait. They can never be costed while "
                    + "that is true, and they are the larger half of most vans' trading.";
            }

            if (UnpostedLineCount > 0)
            {
                yield return
                    $"{UnpostedLineCount:N0} of {LineCount:N0} line(s) are on a sale that has not "
                    + "reached SAP and therefore carry no cost. Any margin here describes the "
                    + "remainder, not the period.";
            }

            if (CurrenciesWithoutMatchingCost.Count > 0)
            {
                yield return
                    "No margin is stated for "
                    + string.Join(", ", CurrenciesWithoutMatchingCost)
                    + $". SAP states a line's cost in {CostCurrency ?? "the company currency"}, and a "
                    + "margin across two currencies would be a subtraction between two kinds of "
                    + "money. Their revenue is reported without it.";
            }

            if (ItemsWithoutCost > 0)
            {
                yield return
                    $"{ItemsWithoutCost:N0} of {ItemCount:N0} item(s) have no usable cost — either "
                    + "nothing they sold reached SAP, or SAP carried no valuation for them on the "
                    + "lines that did. They are reported with revenue and no margin rather than being "
                    + "treated as pure profit.";
            }

            if (ItemsWithNoDescription > 0)
            {
                yield return
                    $"{ItemsWithNoDescription:N0} item(s) sold under a code with no description on "
                    + "any line. They are reported by code alone rather than being dropped.";
            }

            if (RouteCount > 0)
            {
                yield return
                    "Route figures are contribution, not profitability. Nothing in this system "
                    + "records what a route costs to run — no fuel, no driver time, no vehicle or "
                    + "depot cost — so a route's margin here has never met its largest expense. Read "
                    + "it as an input to a keep-or-kill decision rather than as the answer.";
            }

            if (LinesWithNoRoute > 0)
            {
                yield return
                    $"{LinesWithNoRoute:N0} line(s) were sold on a rep-day with no departure record, "
                    + "so nothing says which route they belong to. They are gathered into their own "
                    + "row rather than dropped, and are counted in no route's figures.";
            }
        }
    }
}
