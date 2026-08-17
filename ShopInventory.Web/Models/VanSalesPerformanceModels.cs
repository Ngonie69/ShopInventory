namespace ShopInventory.Web.Models;

/// <summary>
/// The van sales performance report, mirroring the API's <c>VanSalesPerformanceReportResult</c>.
/// </summary>
/// <remarks>
/// Hand-mirrored, like every other API DTO in this project, which makes nullability the thing to get
/// right: a property declared non-nullable here against a value the API can send as null makes
/// System.Text.Json throw, and the page reports "no data" rather than an error.
///
/// Every nullable below is nullable in the API for a reason, and they are not interchangeable with
/// zero. A rate with no denominator, a route nobody opened a day for, an odometer never read, a unit
/// of measure the handset does not send — each is "we cannot say", and a zero in its place is a
/// statement of fact the data does not support.
///
/// The computed properties are re-derived here because computed properties are not serialised. They
/// are kept identical to the API's definitions on purpose; if one is changed, both change.
/// </remarks>
public class VanSalesPerformanceReportResponse
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public VanSalesPerformanceSummary Summary { get; set; } = new();
    public List<VanSalesTerritoryRow> Territories { get; set; } = [];
    public List<VanSalesRouteRow> Routes { get; set; } = [];
    public List<VanSalesRepRow> Reps { get; set; } = [];
    public List<VanSalesItemRow> Items { get; set; } = [];
    public List<VanSalesLapsedItemRow> LapsedItems { get; set; } = [];
    public VanSalesTrend Trend { get; set; } = new();
    public List<VanSalesItemPriceRow> ItemPrices { get; set; } = [];
    public List<VanSalesDropSize> DropSizes { get; set; } = [];
    public VanSalesCoverage Coverage { get; set; } = new();
}

// ── Money and quantity ──────────────────────────────────────────────────────────

/// <summary>One currency's takings at document grain. Gross includes VAT; no net is derivable.</summary>
public class VanSalesMoney
{
    public string Currency { get; set; } = string.Empty;
    public int DocumentCount { get; set; }
    public int DropCount { get; set; }
    public decimal Gross { get; set; }

    public decimal? AverageDocumentValue =>
        DocumentCount > 0 ? decimal.Round(Gross / DocumentCount, 2) : null;

    public decimal? AverageDropSize =>
        DropCount > 0 ? decimal.Round(Gross / DropCount, 2) : null;
}

/// <summary>One currency's takings at line grain. Never mix these with document totals.</summary>
public class VanSalesLineMoney
{
    public string Currency { get; set; } = string.Empty;
    public int LineCount { get; set; }
    public decimal Gross { get; set; }
}

/// <summary>
/// How much moved, in one unit. <c>UoMCode</c> is null on every van sale written to date, so a single
/// "not recorded" bucket is the expected shape rather than a fault.
/// </summary>
public class VanSalesQuantity
{
    public string? UoMCode { get; set; }
    public decimal Quantity { get; set; }
    public int LineCount { get; set; }

    public string DisplayUoM => string.IsNullOrWhiteSpace(UoMCode) ? "unit not recorded" : UoMCode;
}

// ── Route and territory ─────────────────────────────────────────────────────────

public class VanSalesTerritoryRow
{
    public string? Territory { get; set; }
    public int RouteCount { get; set; }
    public int RepCount { get; set; }
    public int TradingDayCount { get; set; }
    public int ProductiveCalls { get; set; }
    public int CustomerCount { get; set; }
    public List<VanSalesMoney> TotalsByCurrency { get; set; } = [];

    public string DisplayTerritory =>
        string.IsNullOrWhiteSpace(Territory) ? "Territory not set" : Territory;
}

public class VanSalesRouteRow
{
    public bool HasRouteDay { get; set; }
    public string? RouteCode { get; set; }
    public string? RouteName { get; set; }
    public string? Territory { get; set; }
    public int RepCount { get; set; }
    public int TradingDayCount { get; set; }
    public int? PlannedCalls { get; set; }
    public int? Calls { get; set; }
    public int ProductiveCalls { get; set; }
    public int CustomerCount { get; set; }
    public int? KilometresTravelled { get; set; }
    public List<VanSalesMoney> TotalsByCurrency { get; set; } = [];

    public double? CallComplianceRate =>
        PlannedCalls is > 0 && Calls is { } calls ? (double)calls / PlannedCalls.Value : null;

    public double? ProductiveCallRate =>
        Calls is > 0 ? (double)ProductiveCalls / Calls.Value : null;

    /// <summary>
    /// What to print in the route column. The three states read differently on purpose — a reader has
    /// to be able to tell a route from a day that named none from a day that was never opened.
    /// </summary>
    public string DisplayRoute
    {
        get
        {
            if (!HasRouteDay)
            {
                return "No departure record";
            }

            if (!string.IsNullOrWhiteSpace(RouteName))
            {
                return RouteName;
            }

            return string.IsNullOrWhiteSpace(RouteCode) ? "Day opened, no route named" : RouteCode;
        }
    }
}

// ── Reps ────────────────────────────────────────────────────────────────────────

public class VanSalesRepRow
{
    public Guid UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? FullName { get; set; }
    public List<string> Routes { get; set; } = [];
    public int TradingDayCount { get; set; }
    public int? Calls { get; set; }
    public int? OutletsVisited { get; set; }
    public int ProductiveCalls { get; set; }
    public int CustomerCount { get; set; }
    public int NewOutlets { get; set; }
    public int NewOutletsWhoBought { get; set; }
    public int ItemCount { get; set; }
    public int? KilometresTravelled { get; set; }
    public List<VanSalesMoney> TotalsByCurrency { get; set; } = [];

    public string DisplayName => string.IsNullOrWhiteSpace(FullName) ? Username : FullName;

    public double? StrikeRate => Calls is > 0 ? (double)ProductiveCalls / Calls.Value : null;

    public double? CallsPerDay =>
        TradingDayCount > 0 && Calls is { } calls ? (double)calls / TradingDayCount : null;
}

// ── Items ───────────────────────────────────────────────────────────────────────

public class VanSalesItemRow
{
    public int Rank { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemDescription { get; set; }
    public int LineCount { get; set; }
    public int DocumentCount { get; set; }
    public int CustomerCount { get; set; }
    public int RepCount { get; set; }
    public int TradingDayCount { get; set; }
    public DateTime FirstSoldOn { get; set; }
    public DateTime LastSoldOn { get; set; }
    public List<VanSalesQuantity> QuantitiesByUoM { get; set; } = [];
    public List<VanSalesLineMoney> TotalsByCurrency { get; set; } = [];

    public string DisplayName =>
        string.IsNullOrWhiteSpace(ItemDescription) ? ItemCode : ItemDescription;
}

public class VanSalesLapsedItemRow
{
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemDescription { get; set; }
    public DateTime LastSoldOn { get; set; }
    public int DaysSinceLastSale { get; set; }
    public int PriorLineCount { get; set; }
    public int PriorCustomerCount { get; set; }
    public List<VanSalesLineMoney> PriorTotalsByCurrency { get; set; } = [];

    public string DisplayName =>
        string.IsNullOrWhiteSpace(ItemDescription) ? ItemCode : ItemDescription;
}

// ── Trend ───────────────────────────────────────────────────────────────────────

public class VanSalesTrend
{
    public List<VanSalesTrendPoint> Daily { get; set; } = [];
    public List<VanSalesSeasonPoint> DayOfWeek { get; set; } = [];
    public List<VanSalesSeasonPoint> Monthly { get; set; } = [];
}

public class VanSalesTrendPoint
{
    public DateTime TradingDate { get; set; }
    public DayOfWeek DayOfWeek { get; set; }
    public int RepsTrading { get; set; }
    public int ProductiveCalls { get; set; }
    public int DocumentCount { get; set; }

    /// <summary>Empty on a day nothing sold. Not a zero row — the day is present and silent.</summary>
    public List<VanSalesMoney> TotalsByCurrency { get; set; } = [];
}

public class VanSalesSeasonPoint
{
    public string Label { get; set; } = string.Empty;
    public int? Year { get; set; }
    public int? Month { get; set; }
    public DayOfWeek? DayOfWeek { get; set; }
    public bool IsPartial { get; set; }
    public int CalendarDayCount { get; set; }
    public int ActiveDayCount { get; set; }
    public int DocumentCount { get; set; }
    public int ProductiveCalls { get; set; }
    public List<VanSalesMoney> TotalsByCurrency { get; set; } = [];

    /// <summary>
    /// Documents per day of this kind that the window contained — including the ones that traded
    /// nothing. Dividing by the active days instead would make a weekday nobody works look average.
    /// </summary>
    public double? DocumentsPerCalendarDay =>
        CalendarDayCount > 0 ? (double)DocumentCount / CalendarDayCount : null;
}

// ── Price realisation ───────────────────────────────────────────────────────────

public class VanSalesItemPriceRow
{
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemDescription { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string? UoMCode { get; set; }
    public int LineCount { get; set; }
    public decimal Quantity { get; set; }
    public decimal Gross { get; set; }
    public decimal WeightedAveragePrice { get; set; }
    public decimal MinUnitPrice { get; set; }
    public decimal MaxUnitPrice { get; set; }
    public List<VanSalesRepPriceRow> Reps { get; set; } = [];

    public decimal? PriceSpreadPercent =>
        WeightedAveragePrice == 0
            ? null
            : decimal.Round((MaxUnitPrice - MinUnitPrice) / WeightedAveragePrice * 100m, 2);

    public string DisplayName =>
        string.IsNullOrWhiteSpace(ItemDescription) ? ItemCode : ItemDescription;
}

public class VanSalesRepPriceRow
{
    public Guid UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? FullName { get; set; }
    public int LineCount { get; set; }
    public decimal Quantity { get; set; }
    public decimal Gross { get; set; }
    public decimal WeightedAveragePrice { get; set; }
    public decimal? VarianceFromItemPercent { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(FullName) ? Username : FullName;
}

// ── Drop size ───────────────────────────────────────────────────────────────────

public class VanSalesDropSize
{
    public string Currency { get; set; } = string.Empty;
    public int DropCount { get; set; }
    public decimal Total { get; set; }
    public decimal Minimum { get; set; }
    public decimal P25 { get; set; }
    public decimal Median { get; set; }
    public decimal P75 { get; set; }
    public decimal Maximum { get; set; }
    public decimal Mean { get; set; }
    public List<VanSalesDropSizeBucket> Buckets { get; set; } = [];

    /// <summary>
    /// How far the middle sits from the average. Well under 1 says a handful of large accounts are
    /// carrying a long tail of calls that barely pay for the stop.
    /// </summary>
    public decimal? MedianToMean => Mean == 0 ? null : decimal.Round(Median / Mean, 2);
}

public class VanSalesDropSizeBucket
{
    public string Label { get; set; } = string.Empty;
    public decimal LowerBound { get; set; }
    public decimal? UpperBound { get; set; }
    public int DropCount { get; set; }
    public decimal Total { get; set; }
    public double? SharePercent { get; set; }
}

// ── Summary and coverage ────────────────────────────────────────────────────────

public class VanSalesPerformanceSummary
{
    public int RepCount { get; set; }
    public int RouteCount { get; set; }
    public int TerritoryCount { get; set; }
    public int TradingDayCount { get; set; }
    public int DocumentCount { get; set; }
    public int? Calls { get; set; }
    public int ProductiveCalls { get; set; }
    public int CustomerCount { get; set; }
    public int ItemCount { get; set; }
    public int NewOutlets { get; set; }
    public int? KilometresTravelled { get; set; }
    public List<VanSalesMoney> TotalsByCurrency { get; set; } = [];

    public double? StrikeRate => Calls is > 0 ? (double)ProductiveCalls / Calls.Value : null;
}

/// <summary>
/// What the report could not see. Rendered as a caveats strip, and hidden entirely when clean.
/// </summary>
public class VanSalesCoverage
{
    public int SaleCount { get; set; }
    public int LineCount { get; set; }
    public int SalesWithoutRouteCustomer { get; set; }
    public int SalesWithoutRouteDay { get; set; }
    public int RepDaysWithoutRouteDay { get; set; }
    public int SalesWithAssumedCurrency { get; set; }
    public int SalesWithoutPaymentMethod { get; set; }
    public int LinesWithoutUoM { get; set; }
    public int LinesWithZeroQuantity { get; set; }
    public int RepsWithoutVisitData { get; set; }
    public int ReferencesInBothSources { get; set; }

    public bool IsClean =>
        SalesWithoutRouteCustomer == 0
        && SalesWithoutRouteDay == 0
        && SalesWithAssumedCurrency == 0
        && LinesWithZeroQuantity == 0
        && RepsWithoutVisitData == 0
        && ReferencesInBothSources == 0;

    /// <summary>
    /// The caveats worth a line each, in the order a reader should meet them. Empty when clean.
    /// </summary>
    public IEnumerable<string> Caveats
    {
        get
        {
            if (ReferencesInBothSources > 0)
            {
                yield return
                    $"{ReferencesInBothSources:N0} reference(s) appear in both the offline and online " +
                    "tables and are counted twice. This is an ingest fault, not a reporting one.";
            }

            if (SalesWithoutRouteDay > 0)
            {
                yield return
                    $"{SalesWithoutRouteDay:N0} sale(s) over {RepDaysWithoutRouteDay:N0} rep-day(s) " +
                    "have no departure record, so they cannot be attributed to a route.";
            }

            if (SalesWithoutRouteCustomer > 0)
            {
                yield return
                    $"{SalesWithoutRouteCustomer:N0} sale(s) name no shop and are grouped as " +
                    "unattributed.";
            }

            if (RepsWithoutVisitData > 0)
            {
                yield return
                    $"{RepsWithoutVisitData:N0} rep(s) sold without any recorded calls, so their " +
                    "strike rate cannot be measured.";
            }

            if (SalesWithAssumedCurrency > 0)
            {
                yield return
                    $"{SalesWithAssumedCurrency:N0} sale(s) state no currency and are counted as USD.";
            }

            if (LinesWithZeroQuantity > 0)
            {
                yield return
                    $"{LinesWithZeroQuantity:N0} line(s) have no quantity and are excluded from the " +
                    "price figures.";
            }
        }
    }
}
