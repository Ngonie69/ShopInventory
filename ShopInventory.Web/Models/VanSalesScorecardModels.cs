namespace ShopInventory.Web.Models;

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
/// <c>Unrated</c> is not a fourth quality band, it is the absence of one. A rep whose calls were
/// never recorded has no strike rate, and colouring that red would accuse somebody of a bad week on
/// the strength of a handset that failed to sync.
/// </remarks>
public enum VanSalesScorecardBand
{
    Unrated,
    Green,
    Amber,
    Red
}

/// <summary>
/// The period scorecard, mirroring the API's <c>VanSalesScorecardReportResult</c>.
/// </summary>
/// <remarks>
/// Hand-mirrored like every other API DTO here, so nullability is the thing to get right: a property
/// declared non-nullable against a value the API can send as null makes System.Text.Json throw, the
/// service's catch turns it into null, and the page reports "no data" rather than an error. The
/// computed properties are re-derived because computed properties are not serialised, and they are
/// kept identical to the API's on purpose; if one changes, both change.
///
/// Almost every null here is a comparison that does not exist rather than a zero. A row that did not
/// trade last period has no movement; a currency taken for the first time has no percentage change;
/// a rep with no call records has no rate and therefore no band. Rendering any of them as zero would
/// turn an absent measurement into a claim, and in each case the claim would be the flattering one.
/// </remarks>
public class VanSalesScorecardReportResponse
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public DateTime PriorFromDate { get; set; }
    public DateTime PriorToDate { get; set; }
    public VanSalesScorecardGrouping Grouping { get; set; }
    public double CallComplianceTarget { get; set; }
    public double StrikeRateTarget { get; set; }
    public VanSalesScorecardSummary Summary { get; set; } = new();
    public List<VanSalesScorecardRow> Rows { get; set; } = [];
    public List<VanSalesScorecardMovement> TakingsMovement { get; set; } = [];
    public VanSalesScorecardQuality Quality { get; set; } = new();
}

// ── Movement ────────────────────────────────────────────────────────────────────

/// <summary>
/// One currency's takings against the same currency's takings last period.
/// </summary>
/// <remarks>
/// Per currency and never folded: USD and ZiG are different money. A currency traded in only one of
/// the two windows appears with the other side null.
/// </remarks>
public class VanSalesScorecardMovement
{
    public string Currency { get; set; } = string.Empty;
    public decimal? Gross { get; set; }
    public decimal? PriorGross { get; set; }

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

public class VanSalesScorecardSummary
{
    public int RowCount { get; set; }
    public int GreenCount { get; set; }
    public int AmberCount { get; set; }
    public int RedCount { get; set; }
    public int UnratedCount { get; set; }
    public int TradingDays { get; set; }
    public int? Calls { get; set; }
    public int? CallsAgainstPlan { get; set; }
    public int? PlannedCalls { get; set; }
    public int ProductiveCalls { get; set; }
    public int OutletsBought { get; set; }
    public int NewOutlets { get; set; }
    public int? Kilometres { get; set; }
    public int? PriorCalls { get; set; }
    public int? PriorCallsAgainstPlan { get; set; }
    public int? PriorPlannedCalls { get; set; }
    public int PriorProductiveCalls { get; set; }
    public int PriorOutletsBought { get; set; }
    public List<VanSalesMoney> TakingsByCurrency { get; set; } = [];

    public double? StrikeRate => Calls is > 0 ? (double)ProductiveCalls / Calls.Value : null;

    public double? PriorStrikeRate =>
        PriorCalls is > 0 ? (double)PriorProductiveCalls / PriorCalls.Value : null;

    /// <summary>Movement in percentage points, not as a proportion of a proportion.</summary>
    public double? StrikeRateMovement =>
        StrikeRate is { } now && PriorStrikeRate is { } then ? now - then : null;

    /// <summary>
    /// Calls made against calls planned. The numerator is <c>CallsAgainstPlan</c>, never
    /// <c>Calls</c>: a day whose plan reads zero is the handset's failed count rather than a plan of
    /// none and is left out of the denominator, so leaving its calls in the numerator counts work
    /// against a plan that excluded it.
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

public class VanSalesScorecardRow
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? SubLabel { get; set; }
    public Guid? UserId { get; set; }
    public double CallComplianceTarget { get; set; }
    public double StrikeRateTarget { get; set; }
    public int TradingDays { get; set; }
    public int? Calls { get; set; }
    public int? CallsAgainstPlan { get; set; }
    public int? PlannedCalls { get; set; }
    public int ProductiveCalls { get; set; }
    public int OutletsBought { get; set; }
    public int NewOutlets { get; set; }
    public int? Kilometres { get; set; }
    public int SalesWithoutTender { get; set; }
    public int SalesWithoutOutlet { get; set; }
    public List<VanSalesMoney> TakingsByCurrency { get; set; } = [];
    public int? PriorCalls { get; set; }
    public int? PriorCallsAgainstPlan { get; set; }
    public int? PriorPlannedCalls { get; set; }
    public int PriorProductiveCalls { get; set; }
    public int PriorOutletsBought { get; set; }
    public List<VanSalesMoney> PriorTakingsByCurrency { get; set; } = [];

    public double? StrikeRate => Calls is > 0 ? (double)ProductiveCalls / Calls.Value : null;

    public double? PriorStrikeRate =>
        PriorCalls is > 0 ? (double)PriorProductiveCalls / PriorCalls.Value : null;

    public double? StrikeRateMovement =>
        StrikeRate is { } now && PriorStrikeRate is { } then ? now - then : null;

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
    public List<VanSalesScorecardMovement> TakingsMovement =>
        TakingsByCurrency
            .Select(row => row.Currency)
            .Concat(PriorTakingsByCurrency.Select(row => row.Currency))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(currency => new VanSalesScorecardMovement
            {
                Currency = currency,
                Gross = TakingsByCurrency
                    .FirstOrDefault(row => string.Equals(row.Currency, currency, StringComparison.OrdinalIgnoreCase))
                    ?.Gross,
                PriorGross = PriorTakingsByCurrency
                    .FirstOrDefault(row => string.Equals(row.Currency, currency, StringComparison.OrdinalIgnoreCase))
                    ?.Gross
            })
            .OrderByDescending(row => row.Gross ?? 0)
            .ThenBy(row => row.Currency, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Where this row sits against its targets. Rates only — money never enters the band, because
    /// takings are per currency and a band that weighed them would be ranking USD against ZiG.
    /// </summary>
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

public class VanSalesScorecardQuality
{
    public int RowCount { get; set; }
    public int UnratedRows { get; set; }
    public int RowsWithNoPriorPeriod { get; set; }
    public int RowsWithNoPlan { get; set; }
    public int SalesWithoutTender { get; set; }
    public int SalesWithoutOutlet { get; set; }
    public bool PriorPeriodEmpty { get; set; }

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
