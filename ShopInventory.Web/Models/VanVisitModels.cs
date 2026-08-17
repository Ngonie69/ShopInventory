namespace ShopInventory.Web.Models;

/// <summary>
/// One van sales call, as the API sends it.
///
/// Mirrors the API's <c>VanVisitDto</c> by hand, like every other portal model, so the nullability
/// has to match or System.Text.Json throws and the page reports no data.
///
/// A near-copy of <see cref="TimesheetEntryDto"/> today, and deliberately not a shared base or an
/// alias of it: a van call and a merchandiser visit are read by different people on different pages
/// and are free to grow apart. Sharing the model is what let the two pages read each other's rows in
/// the first place. There is no channel property here — a van page never needs to ask.
/// </summary>
public class VanVisitDto
{
    public int Id { get; set; }
    public Guid UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? FullName { get; set; }
    public string CustomerCode { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public DateTime CheckInTime { get; set; }
    public DateTime? CheckOutTime { get; set; }
    public double? CheckInLatitude { get; set; }
    public double? CheckInLongitude { get; set; }
    public double? CheckOutLatitude { get; set; }
    public double? CheckOutLongitude { get; set; }
    public string? CheckInNotes { get; set; }
    public string? CheckOutNotes { get; set; }
    public double? DurationMinutes { get; set; }

    public string? CheckInLocationSource { get; set; }
    public string? CheckOutLocationSource { get; set; }
    public double? CheckInLocationAccuracyMetres { get; set; }
    public double? CheckOutLocationAccuracyMetres { get; set; }
    public string? LocationUnavailableReason { get; set; }
    public DateTime? CheckInRecordedAt { get; set; }
    public DateTime? CheckOutRecordedAt { get; set; }

    /// <summary>
    /// The route and truck the round ran on, as they stood on the day.
    ///
    /// All three are nullable and all three are routinely null: they come from the rep's Start Day on
    /// the handset, and a rep who checks into customers without opening a day has calls but no round.
    /// The page says "Route not recorded" rather than leaving the line blank, because a blank reads as
    /// a rendering fault and this is a fact about the day.
    /// </summary>
    public string? RouteCode { get; set; }
    public string? RouteName { get; set; }
    public string? TruckRegNo { get; set; }

    public string StatusDisplay => CheckOutTime.HasValue ? "Completed" : "Open";

    public string DurationDisplay => DurationMinutes.HasValue
        ? $"{(int)(DurationMinutes.Value / 60)}h {(int)(DurationMinutes.Value % 60)}m"
        : "In Progress";

    /// <summary>
    /// Whether this call was queued on a handset with no signal and sent later.
    ///
    /// Recomputed here rather than read from the API, because the API's own copy is a computed
    /// property that System.Text.Json does not send. Both derive it from the same two timestamps and
    /// the same threshold, so they cannot disagree.
    /// </summary>
    public bool WasCapturedOffline =>
        IsLate(CheckInTime, CheckInRecordedAt) || IsLate(CheckOutTime, CheckOutRecordedAt);

    /// <summary>
    /// How far the recorded coordinates can be trusted — "Gps" is a fix taken at the door,
    /// "LastKnown" a remembered one, "None" no fix at all.
    /// </summary>
    public bool HasPreciseCheckInFix =>
        string.Equals(CheckInLocationSource, "Gps", StringComparison.OrdinalIgnoreCase);

    public bool CheckInFixIsStale =>
        string.Equals(CheckInLocationSource, "LastKnown", StringComparison.OrdinalIgnoreCase);

    private static bool IsLate(DateTime? occurred, DateTime? recorded) =>
        occurred.HasValue && recorded.HasValue &&
        recorded.Value - occurred.Value > TimeSpan.FromMinutes(2);
}

public class VanVisitListResponse
{
    public List<VanVisitDto> Entries { get; set; } = [];
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}

/// <summary>
/// Time on the round, summarised per rep. Mirrors the API's <c>VanVisitReportResult</c>.
///
/// <c>Days</c> below are CAT trading days and carry no time of day; every instant on this model —
/// <c>FirstCheckIn</c>, <c>LastCheckOut</c> — is UTC, as everywhere else, and the page converts.
/// </summary>
public class VanVisitReportResponse
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public List<VanVisitReportRepSummary> RepSummaries { get; set; } = [];
    public int TotalCalls { get; set; }
    public int CompletedCalls { get; set; }
    public int OpenCalls { get; set; }
    public int OfflineCalls { get; set; }
    public double TotalHours { get; set; }
    public double AverageCallMinutes { get; set; }
    public int TradingDays { get; set; }
}

public class VanVisitReportRepSummary
{
    public Guid UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? FullName { get; set; }
    public int TotalCalls { get; set; }
    public int CompletedCalls { get; set; }
    public int OpenCalls { get; set; }
    public int OfflineCalls { get; set; }
    public int DistinctCustomers { get; set; }
    public int TradingDays { get; set; }
    public double TotalMinutes { get; set; }
    public double AverageMinutesPerCall { get; set; }
    public List<VanVisitReportDaySummary> Days { get; set; } = [];
    public List<VanVisitReportCustomerSummary> Customers { get; set; } = [];

    /// <summary>The route this rep is on as of the latest day in the period they worked.</summary>
    public string? RouteCode { get; set; }
    public string? RouteName { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(FullName) ? Username : FullName!;

    /// <summary>
    /// Calls closed over calls made. Null rather than zero when the rep made no calls at all — there
    /// is nothing to have completed, and 0% would read as a rep who checked in everywhere and left
    /// nowhere.
    /// </summary>
    public double? CompletionRate => TotalCalls > 0 ? (double)CompletedCalls / TotalCalls : null;

    /// <summary>Calls per trading day, for a rep who worked at least one.</summary>
    public double? CallsPerDay => TradingDays > 0 ? (double)TotalCalls / TradingDays : null;

    /// <summary>
    /// Minutes between the first check-in and the last check-out, summed over the days worked — the
    /// rep's time on the clock for the period.
    ///
    /// Summed per day rather than measured from the first check-in of the period to the last check-out
    /// of it, which would count every night in between as time on the clock.
    /// </summary>
    public double ClockMinutes => Days.Sum(day => day.ClockMinutes ?? 0);

    /// <summary>
    /// Time with customers over time on the clock. The rest is driving, queueing and standing still.
    ///
    /// Null, not zero, when no day in the period has both a first check-in and a last check-out —
    /// there is no clock to divide by, and 0% would read as a rep who visited nobody.
    /// </summary>
    public double? OnSiteShare => ClockMinutes > 0 ? TotalMinutes / ClockMinutes : null;
}

public class VanVisitReportDaySummary
{
    public DateTime Date { get; set; }
    public int CallCount { get; set; }
    public int DistinctCustomers { get; set; }
    public int OpenCalls { get; set; }
    public double TotalMinutes { get; set; }
    public DateTime? FirstCheckIn { get; set; }
    public DateTime? LastCheckOut { get; set; }

    /// <summary>The day's calls in check-in order, which is what the attendance strip draws.</summary>
    public List<VanVisitReportCallSummary> Calls { get; set; } = [];

    /// <summary>The route this round ran on, as snapshotted when the rep started the day.</summary>
    public string? RouteCode { get; set; }
    public string? RouteName { get; set; }

    /// <summary>
    /// Time on the clock: first check-in to last check-out.
    ///
    /// Null when the day never closed — every call still open, or the rep drove off without checking
    /// out of the last one. A day with no end has no length, and taking "now" as the end would make a
    /// rep's on-site share fall for every hour the report is left open.
    /// </summary>
    public double? ClockMinutes => FirstCheckIn is { } first && LastCheckOut is { } last && last > first
        ? (last - first).TotalMinutes
        : null;

    /// <summary>Time with customers over time on the clock, or null when the day has no clock.</summary>
    public double? OnSiteShare => ClockMinutes is { } clock && clock > 0 ? TotalMinutes / clock : null;

    /// <summary>
    /// The gaps between one call ending and the next beginning, longer than
    /// <paramref name="thresholdMinutes"/>.
    ///
    /// Measured here rather than by the API because the threshold is the reader's rule, not the
    /// data's: a supervisor who wants to see every gap over 20 minutes rather than 45 changes it on
    /// the page and the answer changes with no round trip.
    ///
    /// A gap is only counted after a call that closed. The interval after a call that was never
    /// checked out is unmeasurable — the rep may have been inside the shop for all of it — and
    /// charging it as idle would turn one missing tap into an accusation.
    /// </summary>
    public IEnumerable<VanIdleGap> IdleGaps(double thresholdMinutes)
    {
        for (var i = 1; i < Calls.Count; i++)
        {
            if (Calls[i - 1].CheckOutTime is not { } left) continue;

            var minutes = (Calls[i].CheckInTime - left).TotalMinutes;
            if (minutes >= thresholdMinutes)
            {
                yield return new VanIdleGap(left, Calls[i].CheckInTime, minutes);
            }
        }
    }
}

/// <summary>One call on the day's strip. Mirrors the API's <c>VanVisitReportCallSummary</c>.</summary>
public class VanVisitReportCallSummary
{
    public string CustomerCode { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public DateTime CheckInTime { get; set; }

    /// <summary>Null for a call never checked out — drawn as a stub and named, never as zero minutes.</summary>
    public DateTime? CheckOutTime { get; set; }

    public double? DurationMinutes => CheckOutTime is { } out_ && out_ > CheckInTime
        ? (out_ - CheckInTime).TotalMinutes
        : null;
}

/// <summary>A stretch between two calls with nobody being visited. Both instants are UTC.</summary>
public sealed record VanIdleGap(DateTime FromUtc, DateTime ToUtc, double Minutes);

public class VanVisitReportCustomerSummary
{
    public string CustomerCode { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public int CallCount { get; set; }
    public double TotalMinutes { get; set; }
}
