namespace ShopInventory.Web.Models;

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

    public string StatusDisplay => CheckOutTime.HasValue ? "Completed" : "Active";

    public string DurationDisplay => DurationMinutes.HasValue
        ? $"{(int)(DurationMinutes.Value / 60)}h {(int)(DurationMinutes.Value % 60)}m"
        : "In Progress";
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
