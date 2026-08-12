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

    public string DisplayName => string.IsNullOrWhiteSpace(FullName) ? Username : FullName!;

    /// <summary>
    /// Calls closed over calls made. Null rather than zero when the rep made no calls at all — there
    /// is nothing to have completed, and 0% would read as a rep who checked in everywhere and left
    /// nowhere.
    /// </summary>
    public double? CompletionRate => TotalCalls > 0 ? (double)CompletedCalls / TotalCalls : null;

    /// <summary>Calls per trading day, for a rep who worked at least one.</summary>
    public double? CallsPerDay => TradingDays > 0 ? (double)TotalCalls / TradingDays : null;
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
}

public class VanVisitReportCustomerSummary
{
    public string CustomerCode { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public int CallCount { get; set; }
    public double TotalMinutes { get; set; }
}
