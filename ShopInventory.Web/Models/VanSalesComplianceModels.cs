namespace ShopInventory.Web.Models;

/// <summary>
/// The departure compliance report, mirroring the API's <c>DepartureComplianceReportResult</c>.
/// </summary>
/// <remarks>
/// Hand-mirrored, like every other API DTO in this project, which makes nullability the thing to get
/// right: a property declared non-nullable here against a value the API can send as null makes
/// System.Text.Json throw, and the page reports "no data" rather than an error. Every nullable below
/// is nullable in the API for a reason — a rate with no denominator, a day never closed, a route that
/// was never assigned.
/// </remarks>
public class DepartureComplianceReportResponse
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public List<DepartureComplianceDay> Days { get; set; } = [];
    public DepartureComplianceSummary Summary { get; set; } = new();
}

public class DepartureComplianceDay
{
    public int? VanRouteDayId { get; set; }
    public Guid UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? FullName { get; set; }
    public DateTime TradingDate { get; set; }

    public string? Territory { get; set; }
    public string? RouteCode { get; set; }
    public string? RouteName { get; set; }
    public string? TruckRegNo { get; set; }

    /// <summary>Already CAT when it arrives — the API converts. Do not shift it again.</summary>
    public DateTime? TimeOut { get; set; }

    public DateTime? TimeIn { get; set; }

    public int PlannedCustomerCount { get; set; }
    public int CustomersVisited { get; set; }
    public int ProductiveCalls { get; set; }

    public int? RtiOut { get; set; }
    public int? RtiReturned { get; set; }

    public decimal SystemCash { get; set; }
    public decimal SystemEcocash { get; set; }
    public decimal SystemInnbucks { get; set; }
    public decimal SystemOther { get; set; }
    public decimal SystemTotalSales { get; set; }

    public decimal? DeclaredCash { get; set; }
    public decimal? DeclaredEcocash { get; set; }
    public decimal? DeclaredInnbucks { get; set; }

    public string? Currency { get; set; }
    public int NewCustomers { get; set; }

    public int? StartingMileage { get; set; }
    public int? ClosingMileage { get; set; }

    public bool HasDayRecord { get; set; }
    public bool IsClosed { get; set; }
    public string? Notes { get; set; }

    // The API computes these too, but computed properties are not serialised, so they are derived
    // again here from the same figures. Kept identical to the API's definitions on purpose.

    public double? CallComplianceRate =>
        PlannedCustomerCount > 0 ? (double)CustomersVisited / PlannedCustomerCount : null;

    public double? ProductiveCallRate =>
        CustomersVisited > 0 ? (double)ProductiveCalls / CustomersVisited : null;

    public decimal? AverageOrderValue =>
        ProductiveCalls > 0 ? decimal.Round(SystemTotalSales / ProductiveCalls, 2) : null;

    public int? KilometresTravelled =>
        StartingMileage is { } start && ClosingMileage is { } close && close >= start
            ? close - start
            : null;

    public decimal? DeclaredTotal =>
        DeclaredCash is null && DeclaredEcocash is null && DeclaredInnbucks is null
            ? null
            : (DeclaredCash ?? 0) + (DeclaredEcocash ?? 0) + (DeclaredInnbucks ?? 0);

    public decimal? DeclaredVariance =>
        DeclaredTotal is { } declared ? decimal.Round(declared - SystemTotalSales, 2) : null;

    public int? RtiOutstanding =>
        RtiOut is { } issued && RtiReturned is { } returned ? issued - returned : null;

    public string DisplayName => string.IsNullOrWhiteSpace(FullName) ? Username : FullName;
}

public class DepartureComplianceSummary
{
    public int DayCount { get; set; }
    public int PlannedCustomerCount { get; set; }
    public int CustomersVisited { get; set; }
    public int ProductiveCalls { get; set; }
    public decimal TotalSales { get; set; }
    public int NewCustomers { get; set; }
    public int? KilometresTravelled { get; set; }

    public double? CallComplianceRate =>
        PlannedCustomerCount > 0 ? (double)CustomersVisited / PlannedCustomerCount : null;

    public double? ProductiveCallRate =>
        CustomersVisited > 0 ? (double)ProductiveCalls / CustomersVisited : null;

    public decimal? AverageOrderValue =>
        ProductiveCalls > 0 ? decimal.Round(TotalSales / ProductiveCalls, 2) : null;
}

public class RouteDto
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Territory { get; set; }
    public string? TruckRegNo { get; set; }
    public bool IsActive { get; set; }
    public int AssignedUserCount { get; set; }

    public string DisplayLabel => string.IsNullOrWhiteSpace(Territory)
        ? Name
        : $"{Name} — {Territory}";
}
