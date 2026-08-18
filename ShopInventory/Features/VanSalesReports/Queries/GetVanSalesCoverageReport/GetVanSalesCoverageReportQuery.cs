using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VanSalesReports.Queries.GetVanSalesCoverageReport;

/// <summary>How the churn and rate series are bucketed.</summary>
public enum VanSalesCoverageGranularity
{
    Week = 0,
    Month = 1
}

/// <summary>
/// Who the vans are reaching, and who they are losing.
/// </summary>
/// <remarks>
/// The performance report answers what sold. This answers whether the base underneath it is growing
/// or quietly shrinking — which a rising takings figure can hide completely, because a route can
/// bank more money from fewer shops right up until the day it cannot.
///
/// <c>LapseDays</c> defaults to 90 and is deliberately **not**
/// <c>RouteCustomerSalesReporting.DefaultDormantDays</c>, which is 30. That constant answers a
/// different question — whether one shop on one route-customer page has gone quiet — and borrowing
/// its value here would call a third of a normal base lapsed.
/// </remarks>
public sealed record GetVanSalesCoverageReportQuery(
    DateTime FromDate,
    DateTime ToDate,
    Guid? UserId = null,
    string? RouteCode = null,
    int LapseDays = 90,
    VanSalesCoverageGranularity Granularity = VanSalesCoverageGranularity.Month
) : IRequest<ErrorOr<VanSalesCoverageReportResult>>;

public sealed record VanSalesCoverageReportResult(
    DateTime FromDate,
    DateTime ToDate,
    DateTime PriorWindowFrom,
    int LapseDays,
    VanSalesCoverageGranularity Granularity,
    VanSalesCoverageSummaryResult Summary,
    List<VanSalesCoverageTrendPointResult> Trend,
    List<VanSalesRepCoverageResult> Reps,
    List<VanSalesUncoveredOutletResult> UncoveredOutlets,
    VanSalesLocationIntegrityResult LocationIntegrity,
    List<VanSalesChurnPointResult> Churn,
    List<VanSalesLapsedOutletResult> LapsedOutlets,
    List<VanSalesConcentrationResult> Concentration,
    List<VanSalesOutletActivityResult> Outlets,
    VanSalesCoverageQualityResult Quality
);

// ── Summary ─────────────────────────────────────────────────────────────────────

public sealed record VanSalesCoverageSummaryResult(
    int RepCount,
    int? RosterSize,
    int OutletsVisited,
    int OutletsVisitedOnRoster,
    int OutletsVisitedOffRoster,
    int OutletsBought,
    int OutletsUncovered,
    int NewOutlets,
    int ReactivatedOutlets,
    int LapsedOutlets,
    int? Calls,
    int ProductiveCalls,
    int? PlannedCalls,
    int? KilometresTravelled,
    List<VanSalesMoneyResult> TotalsByCurrency)
{
    public double? StrikeRate => Calls is > 0 ? (double)ProductiveCalls / Calls.Value : null;

    /// <summary>
    /// Calls made over calls planned. Null when no departure record in the period stated a plan —
    /// nothing planned means nothing can have been missed.
    /// </summary>
    public double? CallComplianceRate =>
        PlannedCalls is > 0 && Calls is { } calls ? (double)calls / PlannedCalls.Value : null;

    /// <summary>
    /// The share of the roster that was reached at all. Null when the roster is unknown — several
    /// reps have no assigned account, and 0% would read as a rep who visited nobody.
    /// </summary>
    /// <remarks>
    /// The numerator is the calls that landed on a shop the roster actually holds, not every call.
    /// A check-in validates its customer code against nothing, so the raw visit set contains shops
    /// deactivated since, shops on another account's roster, and shops on nobody's — dividing that
    /// by an active-only roster produced rates above 100% on the same page as a non-empty uncovered
    /// register. The calls that fell outside the roster are in <c>OutletsVisitedOffRoster</c>, which
    /// is a finding rather than a rounding error.
    /// </remarks>
    public double? RosterCoverageRate =>
        RosterSize is > 0 ? (double)OutletsVisitedOnRoster / RosterSize.Value : null;
}

// ── B2 / B5: the rate trend ─────────────────────────────────────────────────────

/// <summary>
/// One bucket of the period, carrying the two rates over time.
/// </summary>
/// <remarks>
/// The performance report's trend carries money and documents but no plan and no calls, so it cannot
/// draw either of these lines. This is the series that can.
///
/// Day is not offered as a granularity: a per-day rate across a handful of reps is noise, and the
/// series exists to show a direction rather than a reading.
/// </remarks>
public sealed record VanSalesCoverageTrendPointResult(
    string Label,
    DateTime BucketStart,
    DateTime BucketEnd,
    bool IsPartial,
    int RepsTrading,
    int? PlannedCalls,
    int? Calls,
    int ProductiveCalls,
    int OutletsBought,
    int DaysWithoutPlan,
    int RepDaysWithoutRouteDay)
{
    public double? CallComplianceRate =>
        PlannedCalls is > 0 && Calls is { } calls ? (double)calls / PlannedCalls.Value : null;

    public double? ProductiveCallRate =>
        Calls is > 0 ? (double)ProductiveCalls / Calls.Value : null;
}

// ── Reps ────────────────────────────────────────────────────────────────────────

/// <summary>
/// One rep's coverage: how much of their roster they reached, and how far they drove to do it.
/// </summary>
/// <remarks>
/// <c>OutletsAttributable</c> is the field to read first. A van whose customers are real SAP business
/// partners records no route customer on its sales at all, so every outlet figure below is null for
/// that rep — meaning "the data cannot say", not "none". A zero here would slander a rep who is
/// selling perfectly well.
/// </remarks>
public sealed record VanSalesRepCoverageResult(
    Guid UserId,
    string Username,
    string? FullName,
    string? VanAccountCode,
    List<string> Routes,
    bool OutletsAttributable,
    int? RosterSize,
    int TradingDayCount,
    int? Calls,
    int? OutletsVisited,
    int? OutletsVisitedOnRoster,
    int? OutletsVisitedOffRoster,
    int ProductiveCalls,
    int? OutletsBought,
    int? OutletsUncovered,
    int? PlannedCalls,
    int? KilometresTravelled,
    List<VanSalesEfficiencyResult> EfficiencyByCurrency,
    List<VanSalesMoneyResult> TotalsByCurrency)
{
    public string DisplayName => string.IsNullOrWhiteSpace(FullName) ? Username : FullName;

    public double? StrikeRate => Calls is > 0 ? (double)ProductiveCalls / Calls.Value : null;

    public double? CallComplianceRate =>
        PlannedCalls is > 0 && Calls is { } calls ? (double)calls / PlannedCalls.Value : null;

    /// <summary>
    /// See <see cref="VanSalesCoverageSummaryResult.RosterCoverageRate"/> — the numerator is the
    /// calls that landed on this rep's own roster, never every call they logged.
    /// </summary>
    public double? RosterCoverageRate =>
        RosterSize is > 0 && OutletsVisitedOnRoster is { } visited
            ? (double)visited / RosterSize.Value
            : null;
}

/// <summary>
/// What a kilometre earned, in one currency.
/// </summary>
/// <remarks>
/// Per case is not offered and cannot be: no van sale line carries a unit of measure, so there is no
/// case to divide by.
///
/// Kilometres here are the distance of the days on which <em>this currency</em> traded, so the
/// per-currency figures deliberately do not sum to the parent's total — a day that took both
/// currencies contributes its distance to both. Do not "fix" that by apportioning; there is no basis
/// on which to split a day's driving between two tills.
/// </remarks>
public sealed record VanSalesEfficiencyResult(
    string Currency,
    decimal Gross,
    int DropCount,
    int? Kilometres,
    int DaysWithMileage,
    int DaysWithoutMileage)
{
    public decimal? GrossPerKilometre =>
        Kilometres is > 0 ? decimal.Round(Gross / Kilometres.Value, 2) : null;

    public decimal? KilometresPerDrop =>
        Kilometres is { } km && DropCount > 0 ? decimal.Round((decimal)km / DropCount, 1) : null;
}

// ── B7: the uncovered register ──────────────────────────────────────────────────

/// <summary>Why an outlet appears on the uncovered register.</summary>
public enum VanSalesCoverageGap
{
    /// <summary>On the roster, never visited in this period, and never visited before it either.</summary>
    NeverVisited = 0,

    /// <summary>Visited at some point before this period, but not in it.</summary>
    NotVisitedInWindow = 1,

    /// <summary>Called on during the period, and bought nothing.</summary>
    VisitedNotBought = 2
}

/// <summary>
/// A shop on the books that the period did not reach.
/// </summary>
/// <remarks>
/// This is a coverage register, not a missed-call register, and the difference matters. The vans'
/// day plan is stored as a count — <c>VanRouteDayEntity.PlannedCustomerCount</c> — and the list
/// behind it was never snapshotted, so "these four shops were planned for Tuesday and missed" is
/// unrecoverable. What can be said is which shops on the roster the period never reached.
///
/// The roster is read live, which has two consequences the reader has to be told: a shop added since
/// the period appears here as uncovered for the whole of it, and a shop deactivated since is missing
/// from the register entirely.
///
/// <c>OwningReps</c> is a list because ownership runs through the van's business partner account and
/// several users can hold one. Reducing it to a single name would be a guess dressed as attribution.
/// </remarks>
public sealed record VanSalesUncoveredOutletResult(
    string VanAccountCode,
    string OutletCode,
    string? OutletName,
    string? Phone,
    VanSalesCoverageGap Gap,
    DateTime? LastVisitedOn,
    DateTime? LastPurchaseOn,
    int? DaysSinceLastPurchase,
    DateTime CapturedOn,
    List<string> OwningReps)
{
    public string DisplayName => string.IsNullOrWhiteSpace(OutletName) ? OutletCode : OutletName;

    /// <summary>
    /// True when this shop has never bought in any period the report can see. Distinct from lapsed:
    /// one needs a first sale, the other a win-back, and they are different conversations.
    /// </summary>
    public bool HasNeverBought => LastPurchaseOn is null;
}

// ── B8: location integrity ──────────────────────────────────────────────────────

/// <summary>
/// How much of the location record can be relied on.
/// </summary>
/// <remarks>
/// Not a geofence, and it must never be presented as one. Outlets carry no coordinates anywhere in
/// this system, so "was the rep at the shop" cannot be answered at all. What can be answered is
/// whether the fix on a call was taken from the GPS hardware at the moment of the event, remembered
/// from earlier, or absent — which is what decides whether any future geofence would even have data
/// to work with.
///
/// The per-call flags are already visible on the activity ledger. This is the count, per rep, so a
/// handset that has stopped getting fixes stands out without reading fifty rows.
/// </remarks>
public sealed record VanSalesLocationIntegrityResult(
    int CallCount,
    int CallsWithGpsFix,
    int CallsWithLastKnownFix,
    int CallsWithNoFix,
    int CallsWithoutAccuracy,
    int CallsWithPoorAccuracy,
    int CallsCapturedOffline,
    int PoorAccuracyMetres,
    List<VanSalesRepLocationResult> Reps)
{
    /// <summary>
    /// The share of calls whose position was actually measured. Null on a period with no calls —
    /// not 100%, which is what an empty average would otherwise claim.
    /// </summary>
    public double? GpsFixRate =>
        CallCount > 0 ? (double)CallsWithGpsFix / CallCount : null;

    public bool IsClean =>
        CallsWithNoFix == 0 && CallsWithLastKnownFix == 0 && CallsWithPoorAccuracy == 0;
}

public sealed record VanSalesRepLocationResult(
    Guid UserId,
    string Username,
    string? FullName,
    int CallCount,
    int CallsWithGpsFix,
    int CallsWithLastKnownFix,
    int CallsWithNoFix,
    int CallsWithPoorAccuracy,
    int CallsCapturedOffline)
{
    public string DisplayName => string.IsNullOrWhiteSpace(FullName) ? Username : FullName;

    public double? GpsFixRate => CallCount > 0 ? (double)CallsWithGpsFix / CallCount : null;
}

// ── E1: churn ───────────────────────────────────────────────────────────────────

/// <summary>
/// One bucket of the outlet base: what it opened with, what it gained and lost, what it closed with.
/// </summary>
/// <remarks>
/// The four movements are mutually exclusive by construction, which is the whole reason the arithmetic
/// is worth trusting:
///
/// <c>New</c> requires no purchase ever before this bucket; <c>Reactivated</c> requires one, so they
/// cannot both apply. <c>Lapsed</c> requires no purchase <em>in</em> the bucket, so it excludes both.
/// And lapsing is a transition, not a state — it is counted once, in the bucket where the outlet
/// crossed the line, and never again while it stays quiet.
///
/// <c>UnexplainedMovement</c> publishes the residual of
/// <c>Closing = Opening + New + Reactivated − Lapsed</c>. It should always be zero; it is on the
/// record so that if it ever is not, it shows on the page rather than in someone's spreadsheet six
/// weeks later.
///
/// Activity is derived from sales, never from whether the outlet's row still exists or is flagged
/// active. Shops get deactivated and re-captured; the code and name snapshots on the sale survive it,
/// so history built on sales does not move when somebody tidies the customer list.
/// </remarks>
public sealed record VanSalesChurnPointResult(
    string Label,
    DateTime BucketStart,
    DateTime BucketEnd,
    bool IsPartial,
    bool IsCensored,
    int OpeningActiveOutlets,
    int BuyingOutlets,
    int NewOutlets,
    int ReactivatedOutlets,
    int LapsedOutlets,
    int ClosingActiveOutlets,
    List<VanSalesMoneyResult> TotalsByCurrency,
    List<VanSalesMoneyResult> NewOutletTotalsByCurrency)
{
    public int NetMovement => ClosingActiveOutlets - OpeningActiveOutlets;

    /// <summary>Should always be zero. See the remarks on this record for why it is published.</summary>
    public int UnexplainedMovement =>
        ClosingActiveOutlets - (OpeningActiveOutlets + NewOutlets + ReactivatedOutlets - LapsedOutlets);

    /// <summary>Null on a zero opening base — a rate against nothing is not zero, it is undefined.</summary>
    public double? ChurnRate =>
        OpeningActiveOutlets > 0 ? (double)LapsedOutlets / OpeningActiveOutlets : null;

    public double? GrowthRate =>
        OpeningActiveOutlets > 0 ? (double)NetMovement / OpeningActiveOutlets : null;
}

// ── E3: the lapsed register ─────────────────────────────────────────────────────

/// <summary>
/// A shop that was buying and has stopped — the win-back worklist.
/// </summary>
/// <remarks>
/// <c>LastPurchaseByCurrency</c> is the whole of the final drop, not the final document: two invoices
/// written at one counter are one visit's takings, and a register that showed the smaller of them
/// would understate what is being lost.
///
/// <c>LastSoldByRep</c> and <c>OwningReps</c> are both carried because they diverge, and the
/// divergence is often the reason — a shop that stopped buying when the van changed hands is a
/// different case from one that stopped under the same rep.
/// </remarks>
public sealed record VanSalesLapsedOutletResult(
    string VanAccountCode,
    string OutletCode,
    string? OutletName,
    string? Phone,
    DateTime LastPurchaseOn,
    int DaysSinceLastPurchase,
    DateTime? LastVisitedOn,
    string? LastSoldByRep,
    string? LastRouteCode,
    bool StillOnRoster,
    int PriorPurchaseDayCount,
    List<VanSalesMoneyResult> LastPurchaseByCurrency,
    List<VanSalesMoneyResult> PriorTotalsByCurrency,
    List<string> OwningReps)
{
    public string DisplayName => string.IsNullOrWhiteSpace(OutletName) ? OutletCode : OutletName;
}

// ── E4: concentration ───────────────────────────────────────────────────────────

/// <summary>
/// How much of a route's takings rest on how few shops, in one currency.
/// </summary>
/// <remarks>
/// Sales with no shop on them are excluded from the ranking — they cannot be ranked — but their value
/// is carried as <c>UnattributedGross</c> so a route whose attribution is mostly missing cannot read
/// as a route with a short tail.
///
/// A shop worked by two reps on two routes is ranked separately under each. That is correct and is
/// not deduplicated: the question is what each route depends on.
/// </remarks>
public sealed record VanSalesConcentrationResult(
    bool HasRouteDay,
    string? RouteCode,
    string? RouteName,
    string Currency,
    int OutletCount,
    decimal AttributedGross,
    decimal UnattributedGross,
    decimal Top1Gross,
    decimal Top5Gross,
    decimal Top10Gross,
    int? OutletsForHalfOfGross,
    List<VanSalesOutletShareResult> TopOutlets)
{
    public string DisplayRoute => !HasRouteDay
        ? "No departure record"
        : string.IsNullOrWhiteSpace(RouteName)
            ? RouteCode ?? "Day opened, no route named"
            : RouteName;

    /// <summary>Null when nothing was attributed — no concentration, rather than 0% or 100%.</summary>
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

public sealed record VanSalesOutletShareResult(
    int Rank,
    string OutletCode,
    string? OutletName,
    decimal Gross,
    double SharePercent)
{
    public string DisplayName => string.IsNullOrWhiteSpace(OutletName) ? OutletCode : OutletName;
}

// ── E5: frequency and basket depth ──────────────────────────────────────────────

/// <summary>
/// One shop's behaviour over the period: how often it bought, and how deep the basket was.
/// </summary>
/// <remarks>
/// <c>PurchaseDayCount</c> is the frequency a field manager means — distinct days on which the shop
/// bought, so two invoices at one counter count once. <c>DocumentCount</c> is carried separately
/// rather than folded in.
///
/// Depth is a count of distinct items, never a quantity. Van lines carry no unit of measure, so
/// nothing here divides by one.
/// </remarks>
public sealed record VanSalesOutletActivityResult(
    string VanAccountCode,
    string OutletCode,
    string? OutletName,
    int? VisitCount,
    int PurchaseDayCount,
    int DocumentCount,
    int DistinctItemCount,
    decimal AverageItemsPerPurchase,
    DateTime FirstPurchaseInWindow,
    DateTime LastPurchaseInWindow,
    DateTime? FirstEverPurchaseOn,
    double? AverageDaysBetweenPurchases,
    List<VanSalesMoneyResult> TotalsByCurrency)
{
    public string DisplayName => string.IsNullOrWhiteSpace(OutletName) ? OutletCode : OutletName;

    /// <summary>
    /// The share of calls that ended in a sale. Null when the rep has no visit records — the shop
    /// may have been called on ten times without one of them being written down.
    /// </summary>
    public double? ConversionRate =>
        VisitCount is > 0 ? (double)PurchaseDayCount / VisitCount.Value : null;

    /// <summary>True when this shop's first ever purchase falls inside the reported period.</summary>
    public bool IsNewInWindow =>
        FirstEverPurchaseOn is { } first && first >= FirstPurchaseInWindow;
}

// ── Data quality ────────────────────────────────────────────────────────────────

/// <summary>
/// What this report could not see, counted rather than hidden. Same intent as the performance
/// report's coverage block: every field here is a silent under-report waiting to happen.
/// </summary>
public sealed record VanSalesCoverageQualityResult(
    int SaleCount,
    int RepsWithoutOutletAttribution,
    int RepsWithoutVisitData,
    int RepsWithoutAccount,
    int SalesWithoutRouteCustomer,
    int SalesWithoutRouteDay,
    int DaysWithoutPlan,
    int OutletCodesSharedAcrossAccounts,
    bool RosterIsLive,
    DateTime? EarliestObservedSale)
{
    public bool IsClean =>
        RepsWithoutOutletAttribution == 0
        && RepsWithoutVisitData == 0
        && RepsWithoutAccount == 0
        && SalesWithoutRouteCustomer == 0
        && SalesWithoutRouteDay == 0
        && DaysWithoutPlan == 0;

    /// <summary>The caveats worth a line each, in the order a reader should meet them.</summary>
    public IEnumerable<string> Caveats
    {
        get
        {
            if (RepsWithoutOutletAttribution > 0)
            {
                yield return
                    $"{RepsWithoutOutletAttribution:N0} rep(s) sell to SAP business partners rather than " +
                    "route customers, so no outlet figure can be computed for them at all.";
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
                    $"{RepsWithoutVisitData:N0} rep(s) sold without any recorded calls, so their strike " +
                    "rate and coverage cannot be measured.";
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
