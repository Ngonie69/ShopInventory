namespace ShopInventory.Web.Models;

/// <summary>
/// Which operation a visit belongs to. Mirrors the API's <c>TimesheetChannel</c>; the numbers must
/// match, because System.Text.Json binds this from the integer the API serialises.
/// </summary>
public enum TimesheetChannel
{
    Merchandiser = 0,
    VanSales = 1
}

public class TimesheetEntryDto
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

    public TimesheetChannel Channel { get; set; }
    public string? CheckInLocationSource { get; set; }
    public string? CheckOutLocationSource { get; set; }
    public double? CheckInLocationAccuracyMetres { get; set; }
    public double? CheckOutLocationAccuracyMetres { get; set; }
    public string? LocationUnavailableReason { get; set; }
    public DateTime? CheckInRecordedAt { get; set; }
    public DateTime? CheckOutRecordedAt { get; set; }

    public string StatusDisplay => CheckOutTime.HasValue ? "Completed" : "Active";

    public string DurationDisplay => DurationMinutes.HasValue
        ? $"{(int)(DurationMinutes.Value / 60)}h {(int)(DurationMinutes.Value % 60)}m"
        : "In Progress";

    /// <summary>
    /// Whether this visit was queued on a handset with no signal and sent later.
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

    public string? LocationQualityNote
    {
        get
        {
            if (CheckInFixIsStale)
            {
                var accuracy = CheckInLocationAccuracyMetres is { } metres ? $" (±{metres:F0}m)" : "";
                return $"Last known position{accuracy} — not a fix taken at the door.";
            }

            return string.IsNullOrWhiteSpace(LocationUnavailableReason)
                ? null
                : LocationUnavailableReason;
        }
    }

    private static bool IsLate(DateTime? occurred, DateTime? recorded) =>
        occurred.HasValue && recorded.HasValue &&
        recorded.Value - occurred.Value > TimeSpan.FromMinutes(2);
}

public class TimesheetListResponse
{
    public List<TimesheetEntryDto> Entries { get; set; } = [];
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}

public class TimesheetReportResponse
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public List<TimesheetReportUserSummary> UserSummaries { get; set; } = [];
    public int TotalVisits { get; set; }
    public double TotalHours { get; set; }
    public double AverageVisitMinutes { get; set; }
}

public class TimesheetReportUserSummary
{
    public Guid UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public int TotalVisits { get; set; }
    public int CompletedVisits { get; set; }
    public double TotalMinutes { get; set; }
    public double AverageMinutesPerVisit { get; set; }
    public List<TimesheetReportDailySummary> DailySummaries { get; set; } = [];
    public List<TimesheetReportCustomerSummary> CustomerSummaries { get; set; } = [];
}

public class TimesheetReportDailySummary
{
    public DateTime Date { get; set; }
    public int VisitCount { get; set; }
    public double TotalMinutes { get; set; }
    public DateTime? FirstCheckIn { get; set; }
    public DateTime? LastCheckOut { get; set; }
}

public class TimesheetReportCustomerSummary
{
    public string CustomerCode { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public int VisitCount { get; set; }
    public double TotalMinutes { get; set; }
}
