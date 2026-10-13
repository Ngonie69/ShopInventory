namespace ShopInventory.Web.Models;

/// <summary>
/// The daily truck report from <c>GET api/van-sales/truck-report</c>: a row per route per day, and
/// each route's period against its target.
/// </summary>
/// <remarks>
/// Mirrors the API's <c>VanDailyTruckReportResult</c> by hand, nullability included. The rates and
/// flags arrive in the JSON too, but a property with no setter cannot be read back into, so they are
/// derived again here from the same figures and are kept identical to the API's definitions on purpose.
/// </remarks>
public class VanDailyTruckReportResponse
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public DateTime GeneratedAtUtc { get; set; }

    /// <summary>Monday-to-Friday days in the period: what a period's target is built on.</summary>
    public int WorkingDays { get; set; }

    /// <summary>Those of them that are not in the future.</summary>
    public int WorkingDaysElapsed { get; set; }

    public List<VanDailyTruckSummary> Trucks { get; set; } = [];
    public List<VanDailyTruckDay> Days { get; set; } = [];
    public VanDailyTruckSummary Total { get; set; } = new();
    public VanDailyTruckExceptions Exceptions { get; set; } = new();

    /// <summary>Van takings by a login assigned to no route. In no row, and stated so the rows are not read as the whole.</summary>
    public decimal SalesOutsideRoutes { get; set; }
}

/// <summary>
/// One truck on one day. Money is US dollars with VAT. A null is "not recorded", never zero.
/// </summary>
public class VanDailyTruckDay
{
    public int RouteId { get; set; }
    public string RouteCode { get; set; } = string.Empty;
    public string RouteName { get; set; } = string.Empty;
    public string? Territory { get; set; }
    public DateTime TradingDate { get; set; }
    public bool IsWorkingDay { get; set; }

    /// <summary>The day is over. Today is not, and is not judged against its target or for not reporting.</summary>
    public bool IsComplete { get; set; }

    public bool HasDayRecord { get; set; }
    public bool HasActivity { get; set; }

    /// <summary>Where the truck worked: the office's entry, else the day's own, else the plan.</summary>
    public string? AreaName { get; set; }

    /// <summary><c>Office</c>, <c>Day</c> or <c>Plan</c>; null with no area.</summary>
    public string? AreaSource { get; set; }

    public DateTime? TimeOut { get; set; }
    public DateTime? TimeIn { get; set; }

    /// <summary>Closed on a later date than it was traded, so <see cref="TimeIn"/> is when End Day was tapped.</summary>
    public bool ReturnedLate { get; set; }

    /// <summary>How many shops the day's calls are measured against. Null with no plan. See <see cref="PlanBasis"/>.</summary>
    public int? PlannedCustomers { get; set; }

    /// <summary>Check-ins that count towards the plan: those at shops on the round, or every one against the whole list.</summary>
    public int PlannedCallsMade { get; set; }

    /// <summary>
    /// <c>Round</c>: the shops the truck is scheduled for on the weekday or has called on before in the
    /// same area. <c>List</c>: no round could be named, so the plan is the whole customer list at Start
    /// Day and the rate reads low. Null with no plan.
    /// </summary>
    public string? PlanBasis { get; set; }

    public bool PlanIsWholeList => string.Equals(PlanBasis, "List", StringComparison.Ordinal);

    /// <summary>Distinct shops either rep checked into, on the round or not.</summary>
    public int CustomersVisited { get; set; }
    public int ProductiveCalls { get; set; }
    public int CallsMade { get; set; }
    public int NewCustomers { get; set; }
    public int SalesCount { get; set; }
    public decimal Sales { get; set; }
    public decimal Cash { get; set; }
    public decimal Ecocash { get; set; }
    public decimal Innbucks { get; set; }
    public decimal OtherTender { get; set; }
    public decimal Untendered { get; set; }
    public decimal? OtherCurrencySales { get; set; }

    /// <summary>Sales on account, invoiced in SAP outside the van app and entered by the office. Not part of <see cref="Sales"/>.</summary>
    public decimal? AccountSales { get; set; }

    public decimal? DeclaredTotal { get; set; }
    public int? StartingMileage { get; set; }
    public int? ClosingMileage { get; set; }
    public string OdometerUnit { get; set; } = VanOdometer.Kilometres;
    public int? Kilometres { get; set; }
    public decimal? FuelLitres { get; set; }

    /// <summary>The route's target for the day; null off a working day and on a route with none.</summary>
    public decimal? DailySalesTarget { get; set; }

    public string? Notes { get; set; }

    public double? CallComplianceRate =>
        PlannedCustomers is > 0 ? (double)PlannedCallsMade / PlannedCustomers.Value : null;

    public double? ProductiveCallRate =>
        CustomersVisited > 0 && CallsMade > 0 ? (double)ProductiveCalls / CallsMade : null;

    public decimal? AverageOrderValue =>
        ProductiveCalls > 0 ? decimal.Round(Sales / ProductiveCalls, 2) : null;

    public bool BelowTarget => IsComplete && DailySalesTarget is { } target && Sales < target;

    public bool NotReported => IsComplete && IsWorkingDay && !HasActivity;

    public bool MileageNeedsReview =>
        StartingMileage.HasValue != ClosingMileage.HasValue
        || (StartingMileage is { } start && ClosingMileage is { } close && close < start);

    public bool DistanceWithoutSales => Kilometres is > 0 && Sales == 0m;

    /// <summary>Whether the office has entered anything for the day.</summary>
    public bool HasOfficeEntry =>
        FuelLitres.HasValue || AccountSales.HasValue || AreaSource == "Office" || !string.IsNullOrWhiteSpace(Notes);
}

/// <summary>One route over the period, or the fleet (<see cref="RouteId"/> null).</summary>
public class VanDailyTruckSummary
{
    public int? RouteId { get; set; }
    public string RouteCode { get; set; } = string.Empty;
    public string RouteName { get; set; } = string.Empty;
    public string? Territory { get; set; }
    public decimal? DailySalesTarget { get; set; }

    /// <summary>Daily target times the period's working days. Null with no target.</summary>
    public decimal? PeriodTarget { get; set; }

    public decimal? TargetToDate { get; set; }
    public decimal Sales { get; set; }
    public decimal Ecocash { get; set; }
    public decimal AccountSales { get; set; }
    public int SalesCount { get; set; }
    public int DaysReported { get; set; }
    public int WorkingDaysElapsed { get; set; }
    public int CustomersVisited { get; set; }
    public int ProductiveCalls { get; set; }
    public int NewCustomers { get; set; }
    public int PlannedCustomers { get; set; }
    public int CcrCustomersVisited { get; set; }

    /// <summary><c>Round</c> or <c>List</c>, saying which the period's CCR is over. Null with no plan.</summary>
    public string? PlanBasis { get; set; }

    public bool PlanIsWholeList => string.Equals(PlanBasis, "List", StringComparison.Ordinal);

    /// <summary>
    /// Days that had a list and no round. Where the period has round days these are left out of its
    /// CCR, and this is how many.
    /// </summary>
    public int DaysOnWholeList { get; set; }

    public int PcrProductiveCalls { get; set; }
    public int PcrCallsMade { get; set; }
    public int Kilometres { get; set; }
    public decimal FuelLitres { get; set; }

    public decimal? BalanceToTarget => PeriodTarget is { } target ? target - Sales : null;

    public double? TargetAchieved =>
        PeriodTarget is > 0 ? (double)(Sales / PeriodTarget.Value) : null;

    public double? CallComplianceRate =>
        PlannedCustomers > 0 ? (double)CcrCustomersVisited / PlannedCustomers : null;

    public double? ProductiveCallRate =>
        PcrCallsMade > 0 ? (double)PcrProductiveCalls / PcrCallsMade : null;

    public decimal? AverageOrderValue =>
        ProductiveCalls > 0 ? decimal.Round(Sales / ProductiveCalls, 2) : null;

    public decimal? SalesPerKilometre =>
        Kilometres > 0 ? decimal.Round(Sales / Kilometres, 2) : null;

    public decimal? KilometresPerLitre =>
        FuelLitres > 0 ? decimal.Round(Kilometres / FuelLitres, 2) : null;
}

/// <summary>How many truck-days in the period each check picked out.</summary>
public class VanDailyTruckExceptions
{
    public int BelowTarget { get; set; }
    public int NotReported { get; set; }
    public int MileageNeedsReview { get; set; }
    public int DistanceWithoutSales { get; set; }
    public int ReturnedLate { get; set; }
}

/// <summary>
/// The office's figures for a truck's day, as <c>PUT api/van-sales/truck-days/{routeId}/{date}</c>
/// takes them. A null clears that figure; all four null removes the entry.
/// </summary>
public class VanTruckDayEntryInput
{
    public string? AreaName { get; set; }
    public decimal? FuelLitres { get; set; }
    public decimal? AccountSales { get; set; }
    public string? Notes { get; set; }
}

/// <summary>The entry as it stands after a save. <see cref="HasEntry"/> is false when nothing was left.</summary>
public class VanTruckDayEntry
{
    public int RouteId { get; set; }
    public DateTime TradingDate { get; set; }
    public bool HasEntry { get; set; }
    public string? AreaName { get; set; }
    public decimal? FuelLitres { get; set; }
    public decimal? AccountSales { get; set; }
    public string? Notes { get; set; }
    public string? UpdatedByName { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}
