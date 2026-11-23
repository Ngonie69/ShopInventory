namespace ShopInventory.Web.Models;

/// <summary>Mirrors the API's <c>VanManagementExtrasResult</c>.</summary>
public class VanManagementExtrasResponse
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public List<VanProductDay> Products { get; set; } = [];
    public List<VanBreakageLine> Breakages { get; set; } = [];

    /// <summary>Units of the chosen products sold by logins on no route: in no truck's row.</summary>
    public decimal ProductUnitsOutsideRoutes { get; set; }
}

/// <summary>Mirrors the API's <c>VanProductDayDto</c>: one item sold by one truck on one day.</summary>
public class VanProductDay
{
    public int RouteId { get; set; }
    public DateTime TradingDate { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemName { get; set; }

    /// <summary>Quantity in the unit it was sold in.</summary>
    public decimal Units { get; set; }

    /// <summary>US dollars, VAT included: the same money as the truck report's sales.</summary>
    public decimal Value { get; set; }
}

/// <summary>Mirrors the API's <c>VanBreakageLineDto</c>: one line of a market breakage report.</summary>
public class VanBreakageLine
{
    public int BreakageId { get; set; }
    public int? RouteId { get; set; }
    public DateTime TradingDate { get; set; }
    public string ReportedBy { get; set; } = string.Empty;
    public string? CustomerName { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemDescription { get; set; }
    public string? Reason { get; set; }

    /// <summary>The office's confirmed count where there is one, else what the rep reported.</summary>
    public decimal Units { get; set; }
    public bool IsConfirmed { get; set; }
    public string Status { get; set; } = string.Empty;

    /// <summary>At the period's average van price before VAT; null where the vans sold none of the item.</summary>
    public decimal? ValueBeforeVat { get; set; }
}

/// <summary>
/// The van sales management report: the office's "Vansales Daily Report" workbook, built from the month's
/// truck report and the product and breakage figures beside it. The page and the workbook both read it,
/// so neither can say something the other does not.
/// </summary>
/// <param name="Month">The first of the month reported.</param>
/// <param name="ReportDate">The latest day any truck reported in the month, or the first of the month with none.</param>
/// <param name="Truck">The month's daily truck report.</param>
/// <param name="ProductGroupName">The product group counted; null when none was chosen.</param>
/// <param name="Extras">The product units and breakages; null when they could not be read.</param>
public sealed record VanManagementReport(
    DateTime Month,
    DateTime ReportDate,
    VanDailyTruckReportResponse Truck,
    string? ProductGroupName,
    VanManagementExtrasResponse? Extras)
{
    public IReadOnlyList<VanDailyTruckSummary> Trucks => Truck.Trucks;

    public VanDailyTruckSummary Total => Truck.Total;

    /// <summary>The trucks from best sales to least.</summary>
    public IReadOnlyList<VanDailyTruckSummary> Ranked { get; init; } = [];

    /// <summary>Working days from the first of the month to <see cref="ReportDate"/>, inclusive.</summary>
    public int WorkingDaysToReportDate { get; init; }

    /// <summary>Sales over the working days to the report date; null before any.</summary>
    public decimal? DailyRunRate { get; init; }

    /// <summary>What each working day left in the month must sell to reach the target; null with no target or no days left.</summary>
    public decimal? RequiredPerRemainingDay { get; init; }

    public VanManagementPayments Payments { get; init; } = new(0, 0, 0, 0, 0, 0, 0);

    public IReadOnlyList<VanManagementInsight> Insights { get; init; } = [];

    public IReadOnlyList<VanManagementException> ExceptionChecks { get; init; } = [];

    /// <summary>Every truck-day the office wrote a note on, newest first.</summary>
    public IReadOnlyList<VanDailyTruckDay> OperatingIssues { get; init; } = [];

    /// <summary>Every day of the month, first to last, for choosing the daily view.</summary>
    public IReadOnlyList<DateTime> Dates { get; init; } = [];

    public IReadOnlyList<VanManagementWeek> Weeks { get; init; } = [];

    public VanManagementRouteRanking Routes { get; init; } = new([], 0, 0, 0m);

    public VanManagementProducts Products { get; init; } = VanManagementProducts.Empty;

    public VanManagementBreakages Breakages { get; init; } = VanManagementBreakages.Empty;
}

/// <summary>
/// The month's takings by how they were paid. Ecocash is part of van sales; the workbook's "cash sales"
/// is van sales less Ecocash, and account sales are on their own.
/// </summary>
public sealed record VanManagementPayments(
    decimal VanSales,
    decimal Ecocash,
    decimal Cash,
    decimal Innbucks,
    decimal OtherTender,
    decimal Untendered,
    decimal AccountSales)
{
    /// <summary>Van sales less Ecocash: the workbook's "vansales cash sales".</summary>
    public decimal LessEcocash => VanSales - Ecocash;

    /// <summary>Account sales over van sales; null with no van sales.</summary>
    public double? AccountsShare => VanSales > 0 ? (double)(AccountSales / VanSales) : null;
}

/// <summary>One numbered finding on the insights sheet.</summary>
public sealed record VanManagementInsight(string Title, string Body);

/// <summary>One rule of the exception monitor and how many truck-days it picked out.</summary>
public sealed record VanManagementException(string Rule, int Count, string Meaning);

/// <summary>A Monday-to-Sunday week, clipped to the month.</summary>
public sealed record VanManagementWeek(DateTime Start, DateTime End, IReadOnlyList<VanManagementWeekTruck> Trucks, IReadOnlyList<VanManagementWeekDay> Days)
{
    public decimal Sales => Trucks.Sum(truck => truck.Sales);

    public int Submissions => Trucks.Sum(truck => truck.Submissions);

    public int TrucksReporting => Trucks.Count(truck => truck.Submissions > 0);

    public decimal AccountSales => Trucks.Sum(truck => truck.AccountSales);

    public bool HasData => Submissions > 0;

    public string Label => Start == End ? $"{Start:ddd d MMM}" : $"{Start:d MMM} – {End:d MMM}";
}

/// <summary>One truck over a week. Submissions are the days it reported anything.</summary>
public sealed record VanManagementWeekTruck(int RouteId, string RouteName, decimal Sales, int Submissions, decimal AccountSales, double? Share);

/// <summary>One day of a week across the fleet.</summary>
public sealed record VanManagementWeekDay(DateTime Date, decimal Sales, int Submissions);

/// <summary>
/// The areas the trucks worked, ranked by what was sold there. An area is named as it was recorded, so
/// two spellings of one place are two rows.
/// </summary>
public sealed record VanManagementRouteRanking(
    IReadOnlyList<VanManagementRouteSales> Ranked,
    int SalesRecords,
    int RecordsWithoutArea,
    decimal SalesWithoutArea)
{
    public VanManagementRouteSales? Highest => Ranked.Count > 0 ? Ranked[0] : null;

    /// <summary>The middle rank; the upper of the two middle ones when the count is even.</summary>
    public VanManagementRouteSales? Middle => Ranked.Count > 0 ? Ranked[(Ranked.Count - 1) / 2] : null;

    public VanManagementRouteSales? Lowest => Ranked.Count > 0 ? Ranked[^1] : null;
}

/// <summary>One area: the trucks and days that worked it, and what they sold.</summary>
public sealed record VanManagementRouteSales(string Area, decimal Sales, int Days, string Trucks);

/// <summary>The chosen product group, by truck, by day and by item.</summary>
public sealed record VanManagementProducts(
    IReadOnlyList<VanManagementProductTruck> Trucks,
    IReadOnlyList<VanManagementProductDay> Days,
    IReadOnlyList<VanManagementProductItem> Items,
    decimal UnitsOutsideRoutes)
{
    public static readonly VanManagementProducts Empty = new([], [], [], 0m);

    public decimal Units => Trucks.Sum(truck => truck.Units);

    public decimal Value => Trucks.Sum(truck => truck.Value);
}

public sealed record VanManagementProductTruck(int RouteId, string RouteName, decimal Units, decimal Value);

public sealed record VanManagementProductDay(DateTime Date, decimal Units, decimal Value);

public sealed record VanManagementProductItem(string ItemCode, string? ItemName, decimal Units, decimal Value);

/// <summary>The month's market breakages, by truck, by cause and line by line.</summary>
public sealed record VanManagementBreakages(
    IReadOnlyList<VanManagementBreakageTruck> Trucks,
    IReadOnlyList<VanManagementBreakageCause> Causes,
    IReadOnlyList<VanBreakageLine> Lines)
{
    public static readonly VanManagementBreakages Empty = new([], [], []);

    public decimal Units => Lines.Sum(line => line.Units);

    public decimal Value => Lines.Sum(line => line.ValueBeforeVat ?? 0m);

    /// <summary>Lines that could not be valued because the vans sold none of the item in the month.</summary>
    public int Unvalued => Lines.Count(line => line.ValueBeforeVat is null);
}

/// <summary>One truck's breakages. <c>RouteId</c> is null for reports by a rep on no route.</summary>
public sealed record VanManagementBreakageTruck(int? RouteId, string RouteName, int Lines, decimal Units, decimal Value);

public sealed record VanManagementBreakageCause(string Cause, int Lines, decimal Units, decimal Value);

/// <summary>One truck on one day with its month to that day: the workbook's daily truck card.</summary>
public sealed record VanManagementTruckDay(VanDailyTruckSummary Truck, VanDailyTruckDay? Day, VanManagementToDate ToDate);

/// <summary>A truck's month up to and including a day.</summary>
public sealed record VanManagementToDate(
    decimal Sales,
    decimal Ecocash,
    decimal AccountSales,
    int CustomersVisited,
    int ProductiveCalls,
    int NewCustomers,
    int Kilometres,
    decimal FuelLitres,
    double? CallComplianceRate,
    double? ProductiveCallRate,
    decimal? PeriodTarget)
{
    public decimal? AverageOrderValue => ProductiveCalls > 0 ? decimal.Round(Sales / ProductiveCalls, 2) : null;

    public decimal? BalanceToTarget => PeriodTarget is { } target ? target - Sales : null;

    public double? TargetAchieved => PeriodTarget is > 0 ? (double)(Sales / PeriodTarget.Value) : null;
}
