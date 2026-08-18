namespace ShopInventory.Web.Models;

/// <summary>How the churn and rate series are bucketed. Mirrors the API's enum by name.</summary>
public enum VanSalesCoverageGranularity
{
    Week = 0,
    Month = 1
}

/// <summary>Why an outlet appears on the uncovered register.</summary>
public enum VanSalesCoverageGap
{
    NeverVisited = 0,
    NotVisitedInWindow = 1,
    VisitedNotBought = 2
}

/// <summary>
/// The van sales coverage report, mirroring the API's <c>VanSalesCoverageReportResult</c>.
/// </summary>
/// <remarks>
/// Hand-mirrored, so nullability has to match the API exactly: a property declared non-nullable here
/// against a value the API can send as null makes System.Text.Json throw, and the page reports "no
/// data" rather than an error.
///
/// The nulls in this report carry more meaning than most. A rep with no assigned account has an
/// unknown roster, not an empty one. A rep with no recorded calls is unmeasurable, not a 0% striker.
/// A shop that has never bought is a different conversation from one that has lapsed. Every one of
/// those is a null here and must stay one.
/// </remarks>
public class VanSalesCoverageReportResponse
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public DateTime PriorWindowFrom { get; set; }
    public int LapseDays { get; set; }
    public VanSalesCoverageGranularity Granularity { get; set; }
    public VanSalesCoverageSummary Summary { get; set; } = new();
    public List<VanSalesCoverageTrendPoint> Trend { get; set; } = [];
    public List<VanSalesRepCoverage> Reps { get; set; } = [];
    public List<VanSalesUncoveredOutlet> UncoveredOutlets { get; set; } = [];
    public VanSalesLocationIntegrity LocationIntegrity { get; set; } = new();
    public List<VanSalesChurnPoint> Churn { get; set; } = [];
    public List<VanSalesLapsedOutlet> LapsedOutlets { get; set; } = [];
    public List<VanSalesConcentration> Concentration { get; set; } = [];
    public List<VanSalesOutletActivity> Outlets { get; set; } = [];
    public VanSalesCoverageQuality Quality { get; set; } = new();
}

public class VanSalesCoverageSummary
{
    public int RepCount { get; set; }
    public int? RosterSize { get; set; }
    public int OutletsVisited { get; set; }
    public int OutletsVisitedOnRoster { get; set; }
    public int OutletsVisitedOffRoster { get; set; }
    public int OutletsBought { get; set; }
    public int OutletsUncovered { get; set; }
    public int NewOutlets { get; set; }
    public int ReactivatedOutlets { get; set; }
    public int LapsedOutlets { get; set; }
    public int? Calls { get; set; }
    public int ProductiveCalls { get; set; }
    public int? PlannedCalls { get; set; }
    public int? KilometresTravelled { get; set; }
    public List<VanSalesMoney> TotalsByCurrency { get; set; } = [];

    public double? StrikeRate => Calls is > 0 ? (double)ProductiveCalls / Calls.Value : null;

    public double? CallComplianceRate =>
        PlannedCalls is > 0 && Calls is { } calls ? (double)calls / PlannedCalls.Value : null;

    /// <summary>
    /// The numerator is the calls that landed on a shop the roster holds, not every call. Check-in
    /// validates its code against nothing, so the raw visit set holds shops deactivated since and
    /// shops on nobody's roster — dividing that by an active-only roster read above 100%.
    /// </summary>
    public double? RosterCoverageRate =>
        RosterSize is > 0 ? (double)OutletsVisitedOnRoster / RosterSize.Value : null;
}

public class VanSalesCoverageTrendPoint
{
    public string Label { get; set; } = string.Empty;
    public DateTime BucketStart { get; set; }
    public DateTime BucketEnd { get; set; }
    public bool IsPartial { get; set; }
    public int RepsTrading { get; set; }
    public int? PlannedCalls { get; set; }
    public int? Calls { get; set; }
    public int ProductiveCalls { get; set; }
    public int OutletsBought { get; set; }
    public int DaysWithoutPlan { get; set; }
    public int RepDaysWithoutRouteDay { get; set; }

    public double? CallComplianceRate =>
        PlannedCalls is > 0 && Calls is { } calls ? (double)calls / PlannedCalls.Value : null;

    public double? ProductiveCallRate =>
        Calls is > 0 ? (double)ProductiveCalls / Calls.Value : null;
}

public class VanSalesRepCoverage
{
    public Guid UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? FullName { get; set; }
    public string? VanAccountCode { get; set; }
    public List<string> Routes { get; set; } = [];

    /// <summary>
    /// False for a van that sells to real SAP business partners. Every outlet figure below is then
    /// null meaning "the data cannot say", and the page must render it as such rather than as none.
    /// </summary>
    public bool OutletsAttributable { get; set; }

    public int? RosterSize { get; set; }
    public int TradingDayCount { get; set; }
    public int? Calls { get; set; }
    public int? OutletsVisited { get; set; }
    public int? OutletsVisitedOnRoster { get; set; }
    public int? OutletsVisitedOffRoster { get; set; }
    public int ProductiveCalls { get; set; }
    public int? OutletsBought { get; set; }
    public int? OutletsUncovered { get; set; }
    public int? PlannedCalls { get; set; }
    public int? KilometresTravelled { get; set; }
    public List<VanSalesEfficiency> EfficiencyByCurrency { get; set; } = [];
    public List<VanSalesMoney> TotalsByCurrency { get; set; } = [];

    public string DisplayName => string.IsNullOrWhiteSpace(FullName) ? Username : FullName;

    public double? StrikeRate => Calls is > 0 ? (double)ProductiveCalls / Calls.Value : null;

    public double? CallComplianceRate =>
        PlannedCalls is > 0 && Calls is { } calls ? (double)calls / PlannedCalls.Value : null;

    public double? RosterCoverageRate =>
        RosterSize is > 0 && OutletsVisitedOnRoster is { } visited
            ? (double)visited / RosterSize.Value
            : null;
}

public class VanSalesEfficiency
{
    public string Currency { get; set; } = string.Empty;
    public decimal Gross { get; set; }
    public int DropCount { get; set; }
    public int? Kilometres { get; set; }
    public int DaysWithMileage { get; set; }
    public int DaysWithoutMileage { get; set; }

    public decimal? GrossPerKilometre =>
        Kilometres is > 0 ? decimal.Round(Gross / Kilometres.Value, 2) : null;

    public decimal? KilometresPerDrop =>
        Kilometres is { } km && DropCount > 0 ? decimal.Round((decimal)km / DropCount, 1) : null;
}

public class VanSalesUncoveredOutlet
{
    public string VanAccountCode { get; set; } = string.Empty;
    public string OutletCode { get; set; } = string.Empty;
    public string? OutletName { get; set; }
    public string? Phone { get; set; }
    public VanSalesCoverageGap Gap { get; set; }
    public DateTime? LastVisitedOn { get; set; }
    public DateTime? LastPurchaseOn { get; set; }
    public int? DaysSinceLastPurchase { get; set; }
    public DateTime CapturedOn { get; set; }
    public List<string> OwningReps { get; set; } = [];

    public string DisplayName => string.IsNullOrWhiteSpace(OutletName) ? OutletCode : OutletName;

    public bool HasNeverBought => LastPurchaseOn is null;

    public string GapLabel => Gap switch
    {
        VanSalesCoverageGap.NeverVisited => "Never called on",
        VanSalesCoverageGap.NotVisitedInWindow => "Not called on this period",
        _ => "Called on, bought nothing"
    };
}

public class VanSalesLocationIntegrity
{
    public int CallCount { get; set; }
    public int CallsWithGpsFix { get; set; }
    public int CallsWithLastKnownFix { get; set; }
    public int CallsWithNoFix { get; set; }
    public int CallsWithoutAccuracy { get; set; }
    public int CallsWithPoorAccuracy { get; set; }
    public int CallsCapturedOffline { get; set; }
    public int PoorAccuracyMetres { get; set; }
    public List<VanSalesRepLocation> Reps { get; set; } = [];

    public double? GpsFixRate => CallCount > 0 ? (double)CallsWithGpsFix / CallCount : null;

    public bool IsClean =>
        CallsWithNoFix == 0 && CallsWithLastKnownFix == 0 && CallsWithPoorAccuracy == 0;
}

public class VanSalesRepLocation
{
    public Guid UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? FullName { get; set; }
    public int CallCount { get; set; }
    public int CallsWithGpsFix { get; set; }
    public int CallsWithLastKnownFix { get; set; }
    public int CallsWithNoFix { get; set; }
    public int CallsWithPoorAccuracy { get; set; }
    public int CallsCapturedOffline { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(FullName) ? Username : FullName;

    public double? GpsFixRate => CallCount > 0 ? (double)CallsWithGpsFix / CallCount : null;
}

public class VanSalesChurnPoint
{
    public string Label { get; set; } = string.Empty;
    public DateTime BucketStart { get; set; }
    public DateTime BucketEnd { get; set; }
    public bool IsPartial { get; set; }
    public bool IsCensored { get; set; }
    public int OpeningActiveOutlets { get; set; }
    public int BuyingOutlets { get; set; }
    public int NewOutlets { get; set; }
    public int ReactivatedOutlets { get; set; }
    public int LapsedOutlets { get; set; }
    public int ClosingActiveOutlets { get; set; }
    public List<VanSalesMoney> TotalsByCurrency { get; set; } = [];
    public List<VanSalesMoney> NewOutletTotalsByCurrency { get; set; } = [];

    public int NetMovement => ClosingActiveOutlets - OpeningActiveOutlets;

    /// <summary>Should always be zero. Non-zero means the four movements stopped partitioning.</summary>
    public int UnexplainedMovement =>
        ClosingActiveOutlets - (OpeningActiveOutlets + NewOutlets + ReactivatedOutlets - LapsedOutlets);

    public double? ChurnRate =>
        OpeningActiveOutlets > 0 ? (double)LapsedOutlets / OpeningActiveOutlets : null;

    public double? GrowthRate =>
        OpeningActiveOutlets > 0 ? (double)NetMovement / OpeningActiveOutlets : null;
}

public class VanSalesLapsedOutlet
{
    public string VanAccountCode { get; set; } = string.Empty;
    public string OutletCode { get; set; } = string.Empty;
    public string? OutletName { get; set; }
    public string? Phone { get; set; }
    public DateTime LastPurchaseOn { get; set; }
    public int DaysSinceLastPurchase { get; set; }
    public DateTime? LastVisitedOn { get; set; }
    public string? LastSoldByRep { get; set; }
    public string? LastRouteCode { get; set; }
    public bool StillOnRoster { get; set; }
    public int PriorPurchaseDayCount { get; set; }
    public List<VanSalesMoney> LastPurchaseByCurrency { get; set; } = [];
    public List<VanSalesMoney> PriorTotalsByCurrency { get; set; } = [];
    public List<string> OwningReps { get; set; } = [];

    public string DisplayName => string.IsNullOrWhiteSpace(OutletName) ? OutletCode : OutletName;
}

public class VanSalesConcentration
{
    public bool HasRouteDay { get; set; }
    public string? RouteCode { get; set; }
    public string? RouteName { get; set; }
    public string Currency { get; set; } = string.Empty;
    public int OutletCount { get; set; }
    public decimal AttributedGross { get; set; }
    public decimal UnattributedGross { get; set; }
    public decimal Top1Gross { get; set; }
    public decimal Top5Gross { get; set; }
    public decimal Top10Gross { get; set; }
    public int? OutletsForHalfOfGross { get; set; }
    public List<VanSalesOutletShare> TopOutlets { get; set; } = [];

    public string DisplayRoute => !HasRouteDay
        ? "No departure record"
        : string.IsNullOrWhiteSpace(RouteName)
            ? RouteCode ?? "Day opened, no route named"
            : RouteName;

    public double? Top1SharePercent => Share(Top1Gross);

    public double? Top5SharePercent => Share(Top5Gross);

    public double? Top10SharePercent => Share(Top10Gross);

    public double? UnattributedSharePercent =>
        AttributedGross + UnattributedGross == 0
            ? null
            : (double)decimal.Round(UnattributedGross / (AttributedGross + UnattributedGross) * 100m, 2);

    private double? Share(decimal part) =>
        AttributedGross == 0 ? null : (double)decimal.Round(part / AttributedGross * 100m, 2);
}

public class VanSalesOutletShare
{
    public int Rank { get; set; }
    public string OutletCode { get; set; } = string.Empty;
    public string? OutletName { get; set; }
    public decimal Gross { get; set; }
    public double SharePercent { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(OutletName) ? OutletCode : OutletName;
}

public class VanSalesOutletActivity
{
    public string VanAccountCode { get; set; } = string.Empty;
    public string OutletCode { get; set; } = string.Empty;
    public string? OutletName { get; set; }
    public int? VisitCount { get; set; }
    public int PurchaseDayCount { get; set; }
    public int DocumentCount { get; set; }
    public int DistinctItemCount { get; set; }
    public decimal AverageItemsPerPurchase { get; set; }
    public DateTime FirstPurchaseInWindow { get; set; }
    public DateTime LastPurchaseInWindow { get; set; }
    public DateTime? FirstEverPurchaseOn { get; set; }
    public double? AverageDaysBetweenPurchases { get; set; }
    public List<VanSalesMoney> TotalsByCurrency { get; set; } = [];

    public string DisplayName => string.IsNullOrWhiteSpace(OutletName) ? OutletCode : OutletName;

    public double? ConversionRate =>
        VisitCount is > 0 ? (double)PurchaseDayCount / VisitCount.Value : null;

    public bool IsNewInWindow =>
        FirstEverPurchaseOn is { } first && first >= FirstPurchaseInWindow;
}

public class VanSalesCoverageQuality
{
    public int SaleCount { get; set; }
    public int RepsWithoutOutletAttribution { get; set; }
    public int RepsWithoutVisitData { get; set; }
    public int RepsWithoutAccount { get; set; }
    public int SalesWithoutRouteCustomer { get; set; }
    public int SalesWithoutRouteDay { get; set; }
    public int DaysWithoutPlan { get; set; }
    public int OutletCodesSharedAcrossAccounts { get; set; }
    public bool RosterIsLive { get; set; }
    public DateTime? EarliestObservedSale { get; set; }

    public bool IsClean =>
        RepsWithoutOutletAttribution == 0
        && RepsWithoutVisitData == 0
        && RepsWithoutAccount == 0
        && SalesWithoutRouteCustomer == 0
        && SalesWithoutRouteDay == 0
        && DaysWithoutPlan == 0;

    public IEnumerable<string> Caveats
    {
        get
        {
            if (RepsWithoutOutletAttribution > 0)
            {
                yield return
                    $"{RepsWithoutOutletAttribution:N0} rep(s) sell to SAP business partners rather " +
                    "than route customers, so no outlet figure can be computed for them at all.";
            }

            if (RepsWithoutAccount > 0)
            {
                yield return
                    $"{RepsWithoutAccount:N0} rep(s) have no assigned account, so their roster size " +
                    "and coverage rate are unknown.";
            }

            if (RepsWithoutVisitData > 0)
            {
                yield return
                    $"{RepsWithoutVisitData:N0} rep(s) sold without any recorded calls, so their " +
                    "strike rate and coverage cannot be measured.";
            }

            if (DaysWithoutPlan > 0)
            {
                yield return
                    $"{DaysWithoutPlan:N0} departure record(s) state a plan of zero, which is a failed " +
                    "count rather than a plan. Those days are excluded from call compliance.";
            }

            if (SalesWithoutRouteDay > 0)
            {
                yield return
                    $"{SalesWithoutRouteDay:N0} sale(s) have no departure record, so they cannot be " +
                    "attributed to a route.";
            }

            if (SalesWithoutRouteCustomer > 0)
            {
                yield return
                    $"{SalesWithoutRouteCustomer:N0} sale(s) name no shop and are excluded from every " +
                    "outlet figure.";
            }

            if (OutletCodesSharedAcrossAccounts > 0)
            {
                yield return
                    $"{OutletCodesSharedAcrossAccounts:N0} outlet code(s) are used by more than one van " +
                    "account. They are held apart by account and are not the same shop.";
            }

            if (RosterIsLive)
            {
                yield return
                    "The uncovered register is measured against today's roster, not the roster as it " +
                    "stood during the period: a shop added since reads as uncovered throughout, and one " +
                    "removed since is absent entirely.";
            }
        }
    }
}
