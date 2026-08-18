using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VanSalesReports.Queries.GetVanSalesScorecardReport;

/// <summary>How the league is grouped: one row per rep, or one per route.</summary>
public enum VanSalesScorecardGrouping
{
    Rep,
    Route
}

/// <summary>
/// Where a row sits against its targets.
/// </summary>
/// <remarks>
/// <see cref="Unrated"/> is not a fourth quality band, it is the absence of one. A rep whose calls
/// were never recorded has no strike rate — and colouring that red would accuse somebody of a bad
/// week on the strength of a handset that failed to sync.
/// </remarks>
public enum VanSalesScorecardBand
{
    Unrated,
    Green,
    Amber,
    Red
}

/// <summary>
/// The period scorecard: one row per rep or route, against target, with the direction of travel.
/// </summary>
/// <remarks>
/// This is the reporting plan's F1, F2 and F3 in one report rather than three pages. A daily flash,
/// a weekly route scorecard and a monthly review pack ask the same question of the same underlying
/// measures and differ only in the window, so they are a date range rather than three surfaces that
/// would each have to be kept in step with the other five reports.
///
/// F4, the exception digest, is not here. It shipped as
/// <c>GetVanSalesExceptionsReport</c> — the catalogue defined it as a composite of the cash and
/// leakage questions, and once those turned out to be unbuildable what remained of it was the
/// exception register itself.
///
/// <b>Every measure is taken from <see cref="VanSalesMeasures"/> and nothing is redefined here.</b>
/// That is the whole discipline of a roll-up: it is the one report a manager reads *instead of* the
/// others, so the moment it computes a strike rate its own way it becomes a fifth opinion rather
/// than a summary of four.
///
/// <b>Two things this report deliberately will not do.</b>
///
/// It will not rank on money. Takings are per currency and always a list, so there is no single
/// number to sort a league by; a row's band comes from its rates alone. A route billing in ZiG and
/// one billing in USD are not two positions on one table.
///
/// It will not compare against a window it does not have. The prior period is the equal-length
/// window immediately before this one, and where that window holds no trading at all the movement
/// is null rather than a 100% rise from nothing.
/// </remarks>
public sealed record GetVanSalesScorecardReportQuery(
    DateTime FromDate,
    DateTime ToDate,
    VanSalesScorecardGrouping Grouping = VanSalesScorecardGrouping.Rep,
    Guid? UserId = null,
    double CallComplianceTarget = 0.95,
    double StrikeRateTarget = 0.75
) : IRequest<ErrorOr<VanSalesScorecardReportResult>>;

public sealed record VanSalesScorecardReportResult(
    DateTime FromDate,
    DateTime ToDate,
    DateTime PriorFromDate,
    DateTime PriorToDate,
    VanSalesScorecardGrouping Grouping,
    double CallComplianceTarget,
    double StrikeRateTarget,
    VanSalesScorecardSummaryResult Summary,
    List<VanSalesScorecardRowResult> Rows,
    List<VanSalesScorecardMovementResult> TakingsMovement,
    VanSalesScorecardQualityResult Quality
);

/// <summary>
/// One currency's takings against the same currency's takings last period.
/// </summary>
/// <remarks>
/// Per currency and never folded, for the reason stated on <c>VanSalesMoney</c>: USD and ZiG are
/// different money. A currency traded in only one of the two windows still gets a row, with the
/// absent side reported as null rather than as zero — a currency the fleet has just started taking
/// has not grown infinitely, it has no comparison.
/// </remarks>
public sealed record VanSalesScorecardMovementResult(
    string Currency,
    decimal? Gross,
    decimal? PriorGross)
{
    public decimal? Movement => Gross is { } now && PriorGross is { } then ? now - then : null;

    /// <summary>
    /// Proportional change, or null when there is nothing to divide by. A period that starts from
    /// nothing has no percentage, and rendering one would be a division by zero dressed up.
    /// </summary>
    public double? PercentChange =>
        Gross is { } now && PriorGross is { } then && then != 0
            ? (double)((now - then) / Math.Abs(then))
            : null;
}

// ── Summary ─────────────────────────────────────────────────────────────────────

public sealed record VanSalesScorecardSummaryResult(
    int RowCount,
    int GreenCount,
    int AmberCount,
    int RedCount,
    int UnratedCount,
    int TradingDays,
    int? Calls,
    int? CallsAgainstPlan,
    int? PlannedCalls,
    int ProductiveCalls,
    int OutletsBought,
    int NewOutlets,
    int? Kilometres,
    int? PriorCalls,
    int? PriorCallsAgainstPlan,
    int? PriorPlannedCalls,
    int PriorProductiveCalls,
    int PriorOutletsBought,
    List<VanSalesMoneyResult> TakingsByCurrency)
{
    public double? StrikeRate => Calls is > 0 ? (double)ProductiveCalls / Calls.Value : null;

    public double? PriorStrikeRate =>
        PriorCalls is > 0 ? (double)PriorProductiveCalls / PriorCalls.Value : null;

    /// <summary>Movement in percentage points, not as a proportion of a proportion.</summary>
    public double? StrikeRateMovement =>
        StrikeRate is { } now && PriorStrikeRate is { } then ? now - then : null;

    /// <summary>
    /// Calls made against calls planned. The numerator is <see cref="CallsAgainstPlan"/>, never
    /// <see cref="Calls"/>: a day whose plan reads zero is the handset's failed count rather than a
    /// plan of none and is left out of the denominator, so leaving its calls in the numerator counts
    /// work against a plan that excluded it. The coverage report reported over 100% that way.
    /// </summary>
    public double? CallComplianceRate =>
        PlannedCalls is > 0 && CallsAgainstPlan is { } calls ? (double)calls / PlannedCalls.Value : null;

    public double? PriorCallComplianceRate =>
        PriorPlannedCalls is > 0 && PriorCallsAgainstPlan is { } calls
            ? (double)calls / PriorPlannedCalls.Value
            : null;

    public double? CallComplianceMovement =>
        CallComplianceRate is { } now && PriorCallComplianceRate is { } then ? now - then : null;

    public int? OutletsBoughtMovement => OutletsBought - PriorOutletsBought;

    /// <summary>
    /// The share of rows carrying a band at all. Low here means the league is describing a fraction
    /// of the fleet, and a reader ranking on it is ranking the reps whose handsets synced.
    /// </summary>
    public double? RatedShare => RowCount > 0 ? (double)(RowCount - UnratedCount) / RowCount : null;
}

// ── The league ──────────────────────────────────────────────────────────────────

/// <summary>
/// One rep or one route, this period against last.
/// </summary>
/// <remarks>
/// Both windows' raw counters are carried and every rate and movement is computed from them here,
/// rather than the handler sending pre-computed rates. One set of numbers cannot disagree with
/// itself, and a reader checking a movement by hand gets the same answer the page did.
/// </remarks>
public sealed record VanSalesScorecardRowResult(
    string Key,
    string Label,
    string? SubLabel,
    Guid? UserId,
    double CallComplianceTarget,
    double StrikeRateTarget,
    int TradingDays,
    int? Calls,
    int? CallsAgainstPlan,
    int? PlannedCalls,
    int ProductiveCalls,
    int OutletsBought,
    int NewOutlets,
    int? Kilometres,
    int SalesWithoutTender,
    int SalesWithoutOutlet,
    List<VanSalesMoneyResult> TakingsByCurrency,
    int? PriorCalls,
    int? PriorCallsAgainstPlan,
    int? PriorPlannedCalls,
    int PriorProductiveCalls,
    int PriorOutletsBought,
    List<VanSalesMoneyResult> PriorTakingsByCurrency)
{
    public double? StrikeRate => Calls is > 0 ? (double)ProductiveCalls / Calls.Value : null;

    public double? PriorStrikeRate =>
        PriorCalls is > 0 ? (double)PriorProductiveCalls / PriorCalls.Value : null;

    public double? StrikeRateMovement =>
        StrikeRate is { } now && PriorStrikeRate is { } then ? now - then : null;

    /// <summary>
    /// Calls made against calls planned. The numerator is <see cref="CallsAgainstPlan"/>, never
    /// <see cref="Calls"/>: a day whose plan reads zero is the handset's failed count rather than a
    /// plan of none and is left out of the denominator, so leaving its calls in the numerator counts
    /// work against a plan that excluded it. The coverage report reported over 100% that way.
    /// </summary>
    public double? CallComplianceRate =>
        PlannedCalls is > 0 && CallsAgainstPlan is { } calls ? (double)calls / PlannedCalls.Value : null;

    public double? PriorCallComplianceRate =>
        PriorPlannedCalls is > 0 && PriorCallsAgainstPlan is { } calls
            ? (double)calls / PriorPlannedCalls.Value
            : null;

    public double? CallComplianceMovement =>
        CallComplianceRate is { } now && PriorCallComplianceRate is { } then ? now - then : null;

    public int OutletsBoughtMovement => OutletsBought - PriorOutletsBought;

    /// <summary>
    /// This period's takings against last period's, per currency. A currency present in one window
    /// only appears with the other side null.
    /// </summary>
    public List<VanSalesScorecardMovementResult> TakingsMovement =>
        TakingsByCurrency
            .Select(row => row.Currency)
            .Concat(PriorTakingsByCurrency.Select(row => row.Currency))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(currency => new VanSalesScorecardMovementResult(
                Currency: currency,
                Gross: TakingsByCurrency
                    .FirstOrDefault(row => string.Equals(row.Currency, currency, StringComparison.OrdinalIgnoreCase))
                    ?.Gross,
                PriorGross: PriorTakingsByCurrency
                    .FirstOrDefault(row => string.Equals(row.Currency, currency, StringComparison.OrdinalIgnoreCase))
                    ?.Gross))
            .OrderByDescending(row => row.Gross ?? 0)
            .ThenBy(row => row.Currency, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Where this row sits against its targets.
    /// </summary>
    /// <remarks>
    /// Rates only. Money never enters the band, because takings are per currency and a band that
    /// weighed them would be ranking USD against ZiG.
    ///
    /// A row with neither rate available is <see cref="VanSalesScorecardBand.Unrated"/> — the
    /// measures are missing, not bad. Red is reserved for a rate that is more than ten points under
    /// its target, which is the distance at which a gap stops being a normal week.
    /// </remarks>
    public VanSalesScorecardBand Band
    {
        get
        {
            var rates = new List<(double Rate, double Target)>();

            if (StrikeRate is { } strike)
            {
                rates.Add((strike, StrikeRateTarget));
            }

            if (CallComplianceRate is { } compliance)
            {
                rates.Add((compliance, CallComplianceTarget));
            }

            if (rates.Count == 0)
            {
                return VanSalesScorecardBand.Unrated;
            }

            if (rates.Any(pair => pair.Rate < pair.Target - 0.10))
            {
                return VanSalesScorecardBand.Red;
            }

            return rates.Any(pair => pair.Rate < pair.Target)
                ? VanSalesScorecardBand.Amber
                : VanSalesScorecardBand.Green;
        }
    }

    /// <summary>Capture problems on this row, which the exception register carries in full.</summary>
    public int CaptureGaps => SalesWithoutTender + SalesWithoutOutlet;
}

// ── Quality ─────────────────────────────────────────────────────────────────────

public sealed record VanSalesScorecardQualityResult(
    int RowCount,
    int UnratedRows,
    int RowsWithNoPriorPeriod,
    int RowsWithNoPlan,
    int SalesWithoutTender,
    int SalesWithoutOutlet,
    bool PriorPeriodEmpty)
{
    public bool IsClean =>
        UnratedRows == 0
        && RowsWithNoPriorPeriod == 0
        && RowsWithNoPlan == 0
        && SalesWithoutTender == 0
        && SalesWithoutOutlet == 0
        && !PriorPeriodEmpty;

    public IEnumerable<string> Caveats
    {
        get
        {
            if (PriorPeriodEmpty)
            {
                yield return
                    "The preceding period holds no van trading at all, so every movement on this page "
                    + "is unavailable rather than a rise from nothing.";
            }
            else if (RowsWithNoPriorPeriod > 0)
            {
                yield return
                    $"{RowsWithNoPriorPeriod:N0} row(s) did not trade in the preceding period, so their "
                    + "movement is unavailable. A first period is not growth of infinity.";
            }

            if (UnratedRows > 0)
            {
                yield return
                    $"{UnratedRows:N0} row(s) carry no band because no call was recorded against them. "
                    + "That is a handset that did not sync rather than a rep who did not work, so they "
                    + "are left uncoloured rather than marked down.";
            }

            if (RowsWithNoPlan > 0)
            {
                yield return
                    $"{RowsWithNoPlan:N0} row(s) stated no planned call count, so they have no call "
                    + "compliance and are banded on strike rate alone.";
            }

            if (SalesWithoutTender > 0 || SalesWithoutOutlet > 0)
            {
                yield return
                    $"{SalesWithoutTender:N0} sale(s) record no payment method and "
                    + $"{SalesWithoutOutlet:N0} name no outlet. They are in the takings but not in the "
                    + "outlet or tender figures, so those two will not reconcile to the money.";
            }

            yield return
                "Rows are banded on their rates alone. Takings are per currency and are never ranked, "
                + "so a route billing in ZiG and one billing in USD hold no position against each other.";

            yield return
                "This page summarises the other van reports and adds nothing to them. Where a figure "
                + "here disagrees with the report it came from, the report is right and this is a bug.";
        }
    }
}
