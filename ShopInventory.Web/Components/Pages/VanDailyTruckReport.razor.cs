using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

public partial class VanDailyTruckReport : IDisposable
{
    [Inject] private IVanSalesReportService VanSalesReportService { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;

    private const string CheckQueryName = "check";

    [SupplyParameterFromQuery(Name = VanReportLinks.From)] private string? FromQuery { get; set; }
    [SupplyParameterFromQuery(Name = VanReportLinks.To)] private string? ToQuery { get; set; }
    [SupplyParameterFromQuery(Name = CheckQueryName)] private string? CheckQuery { get; set; }

    private readonly LatestLoad loads = new();
    [Inject] private IReportExportService ExportService { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;

    private VanDailyTruckReportResponse? report;

    private DateTime? fromDate;
    private DateTime? toDate;

    private int? openRoute;

    /// <summary>The check the list is narrowed to; null shows every truck.</summary>
    private DayCheck? filter;
    private string? error;
    private bool isLoading = true;
    private bool isSaving;
    private bool isExporting;

    // The one day whose office entry is open, and what has been typed into it.
    private VanDailyTruckDay? editing;
    private string? entryArea;
    private decimal? entryFuel;
    private decimal? entryAccount;
    private string? entryNotes;
    private string? entryError;

    /// <summary>
    /// Opens on the period a link or a refresh names, else on the whole of this month: the period the
    /// workbook this replaces was kept for. The whole month and not the month so far, because the
    /// period's target is built on its working days, and a target that ends today cannot say how far
    /// behind the month the trucks are. The days still to come are not listed, so nothing is judged
    /// before it happens.
    /// </summary>
    protected override async Task OnInitializedAsync()
    {
        var today = VanReportPeriod.TodayCat;
        var monthStart = new DateTime(today.Year, today.Month, 1);

        fromDate = VanReportPeriod.ParseDay(FromQuery) ?? monthStart;
        toDate = VanReportPeriod.ParseDay(ToQuery) ?? monthStart.AddMonths(1).AddDays(-1);

        if (Enum.TryParse<DayCheck>(CheckQuery, ignoreCase: true, out var check))
        {
            filter = check;
        }

        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (fromDate is not { } from || toDate is not { } to)
        {
            error = "Choose both dates.";
            return;
        }

        var token = loads.Begin();
        isLoading = true;
        error = null;
        CancelEntry();
        StateHasChanged();

        try
        {
            // The new period's trucks or nothing: never the old month under an error about the new one.
            var result = await VanSalesReportService.GetDailyTruckReportAsync(from, to, token);
            report = result.Value;
            error = result.Error;

            if (report is not null)
            {
                WriteQuery();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A newer load replaced this one and owns the page now.
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                isLoading = false;
                StateHasChanged();
            }
        }
    }

    /// <summary>The period and the check the list is narrowed to, in the page's own URL.</summary>
    private void WriteQuery()
    {
        var values = VanReportLinks.Query(report?.FromDate ?? fromDate, report?.ToDate ?? toDate);
        values[CheckQueryName] = filter?.ToString();
        VanReportLinks.WriteQuery(Navigation, values);
    }

    public void Dispose() => loads.Dispose();

    /// <summary>
    /// Downloads the report on screen as a workbook: the period that was run, not whatever the date
    /// boxes have been changed to since.
    /// </summary>
    private async Task ExportAsync()
    {
        if (report is null || isExporting)
        {
            return;
        }

        isExporting = true;
        error = null;

        try
        {
            var bytes = ExportService.ExportVanDailyTruckReportToExcel(report);
            var fileName = $"VanDailyTruckReport_{report.FromDate:yyyyMMdd}-{report.ToDate:yyyyMMdd}.xlsx";

            await JS.InvokeVoidAsync("downloadFile", fileName, Convert.ToBase64String(bytes));
        }
        catch (Exception ex)
        {
            error = $"The export could not be downloaded: {ex.Message}";
        }
        finally
        {
            isExporting = false;
        }
    }

    private IEnumerable<VanDailyTruckDay> DaysOf(int routeId) =>
        (report?.Days ?? []).Where(day => day.RouteId == routeId).OrderBy(day => day.TradingDate);

    // — The checks, and narrowing the list to one ————————————————————————————————————————————

    /// <summary>The five checks the API counts in <see cref="VanDailyTruckExceptions"/>, in that order.</summary>
    private enum DayCheck
    {
        NotReported,
        BelowTarget,
        DistanceWithoutSales,
        MileageNeedsReview,
        ReturnedLate
    }

    private static bool Fails(VanDailyTruckDay day, DayCheck check) => check switch
    {
        DayCheck.NotReported => day.NotReported,
        DayCheck.BelowTarget => day.BelowTarget,
        DayCheck.DistanceWithoutSales => day.DistanceWithoutSales,
        DayCheck.MileageNeedsReview => day.MileageNeedsReview,
        DayCheck.ReturnedLate => day.ReturnedLate,
        _ => false
    };

    /// <summary>Each check with the API's count of the truck-days it picked out.</summary>
    private IEnumerable<(DayCheck Check, int Count, string Label)> Checks()
    {
        var counts = report?.Exceptions ?? new VanDailyTruckExceptions();
        yield return (DayCheck.NotReported, counts.NotReported, "not reported");
        yield return (DayCheck.BelowTarget, counts.BelowTarget, "below target");
        yield return (DayCheck.DistanceWithoutSales, counts.DistanceWithoutSales, "distance, no sales");
        yield return (DayCheck.MileageNeedsReview, counts.MileageNeedsReview, "mileage to check");
        yield return (DayCheck.ReturnedLate, counts.ReturnedLate, "closed a day late");
    }

    private void ToggleFilter(DayCheck? check)
    {
        filter = filter == check ? null : check;
        CancelEntry();
        WriteQuery();
    }

    /// <summary>The trucks with at least one day the chosen check picks out; every truck with none chosen.</summary>
    private IEnumerable<VanDailyTruckSummary> TrucksShown() =>
        filter is not { } check
            ? report?.Trucks ?? []
            : (report?.Trucks ?? []).Where(truck => DaysOf(truck.RouteId!.Value).Any(day => Fails(day, check)));

    /// <summary>The chosen check picked this day out, so it is outlined wherever it is drawn.</summary>
    private bool Marked(VanDailyTruckDay day) => filter is { } check && Fails(day, check);

    // — The strip: one bar per day, against the day's target ———————————————————————————————

    private const double StripHeight = 34;

    /// <summary>
    /// The strip's top is this many times the daily target, so a day well over it still reads as over
    /// and the target line sits at the same height on every truck.
    /// </summary>
    private const double StripCeiling = 1.4;

    /// <summary>Where the target line sits, from the strip's foot. Null on a truck with no target.</summary>
    private static string? TargetLine(VanDailyTruckSummary truck) =>
        truck.DailySalesTarget is > 0 ? Px(StripHeight / StripCeiling) : null;

    private static string BarClass(VanDailyTruckDay day) =>
        !day.IsComplete ? "vsk-bar-today"
        : day.NotReported ? "vsk-bar-missing"
        : day.DistanceWithoutSales ? "vsk-bar-nosale"
        : !day.HasActivity ? "vsk-bar-quiet"
        : day.DailySalesTarget is not > 0 ? "vsk-bar-plain"
        : day.Sales >= day.DailySalesTarget ? "vsk-bar-met"
        : "vsk-bar-below";

    /// <summary>
    /// A day's height: its sales against the strip's ceiling, or against the truck's best day where the
    /// day has no target (a weekend, a route with none). A sale too small to see is still drawn.
    /// </summary>
    private string BarHeight(VanDailyTruckDay day)
    {
        if (day.NotReported)
        {
            return Px(StripHeight);
        }

        var scale = day.DailySalesTarget is > 0 and { } target
            ? (double)target * StripCeiling
            : (double)DaysOf(day.RouteId).Select(other => other.Sales).DefaultIfEmpty(0m).Max();

        var height = scale > 0 ? Math.Min(StripHeight, (double)day.Sales / scale * StripHeight) : 0;

        if (day.Sales > 0)
        {
            height = Math.Max(height, 3);
        }

        if (day.DistanceWithoutSales)
        {
            height = 4;
        }
        else if (!day.IsComplete)
        {
            height = Math.Max(height, 6);
        }

        return Px(height);
    }

    private static string BarTitle(VanDailyTruckDay day)
    {
        var date = day.TradingDate.ToString("ddd d MMM");

        if (!day.IsComplete)
        {
            return day.HasActivity ? $"{date}: {Money(day.Sales)} so far today" : $"{date}: not out yet today";
        }

        if (day.NotReported)
        {
            return $"{date}: nothing reported";
        }

        return day.DailySalesTarget is > 0 and { } target
            ? $"{date}: {Money(day.Sales)} against {Money(target)}"
            : $"{date}: {Money(day.Sales)}";
    }

    private static string StripLabel(VanDailyTruckSummary truck) =>
        $"{truck.RouteName}: {truck.DaysReported} of {truck.WorkingDaysElapsed} days reported";

    // — Pace: sales against the target for the working days so far ———————————————————————————

    /// <summary>How much of the period's target a bar fills, capped at the whole bar.</summary>
    private static string Fill(VanDailyTruckSummary summary) =>
        summary.PeriodTarget is > 0 and { } target
            ? Share(Math.Min(1.0, (double)(summary.Sales / target)))
            : "0%";

    /// <summary>Where the target for the days so far falls along the period's.</summary>
    private static string PaceMark(VanDailyTruckSummary summary) =>
        summary is { PeriodTarget: > 0 and { } target, TargetToDate: { } paced }
            ? Share(Math.Min(1.0, (double)(paced / target)))
            : "0%";

    private static bool Behind(VanDailyTruckSummary summary) =>
        summary.TargetToDate is { } paced && summary.Sales < paced;

    private static string? PaceText(VanDailyTruckSummary summary, string ahead, string behind)
    {
        if (summary.TargetToDate is not { } paced)
        {
            return null;
        }

        var gap = summary.Sales - paced;
        return gap >= 0 ? $"{Money(gap)} {ahead}" : $"{Money(-gap)} {behind}";
    }

    /// <summary>The day the period has reached, said under the fleet's pace mark.</summary>
    private string PaceDay =>
        report is null ? string.Empty
        : report.WorkingDaysElapsed >= report.WorkingDays ? $"all {report.WorkingDays} working days"
        : $"today · day {report.WorkingDaysElapsed} of {report.WorkingDays}";

    /// <summary>A day's sales against its target, signed, for under the figure.</summary>
    private static string? Versus(VanDailyTruckDay day) =>
        day.IsComplete && day.HasActivity && day.DailySalesTarget is > 0 and { } target
            ? (day.Sales >= target ? "+" : "−") + Money(Math.Abs(day.Sales - target))
            : null;

    // Invariant, because these go into a style attribute and a decimal comma would void the rule.
    private static string Px(double value) => value.ToString("0.#", CultureInfo.InvariantCulture) + "px";

    private static string Share(double fraction) => (fraction * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";

    private void ToggleRoute(int routeId)
    {
        openRoute = openRoute == routeId ? null : routeId;
        CancelEntry();
    }

    private void StartEntry(VanDailyTruckDay day)
    {
        editing = day;
        entryError = null;

        // Only what the office itself said is put back in the box. A planned or handset area is
        // shown as the hint, so saving the form untouched does not turn a plan into an entry.
        entryArea = day.AreaSource == "Office" ? day.AreaName : null;
        entryFuel = day.FuelLitres;
        entryAccount = day.AccountSales;
        entryNotes = day.Notes;
    }

    private void CancelEntry()
    {
        editing = null;
        entryError = null;
    }

    private async Task SaveEntryAsync()
    {
        if (editing is not { } day || fromDate is not { } from || toDate is not { } to)
        {
            return;
        }

        isSaving = true;
        entryError = null;

        try
        {
            var (_, refusal) = await VanSalesReportService.SaveTruckDayEntryAsync(
                day.RouteId,
                day.TradingDate,
                new VanTruckDayEntryInput
                {
                    AreaName = entryArea,
                    FuelLitres = entryFuel,
                    AccountSales = entryAccount,
                    Notes = entryNotes
                });

            if (refusal is not null)
            {
                entryError = refusal;
                return;
            }

            // Read back rather than patched in place: the totals, the km a litre and the area's
            // source all move with one entry, and the server is the one that works them out.
            var refreshed = await VanSalesReportService.GetDailyTruckReportAsync(from, to);

            if (refreshed.Value is null)
            {
                entryError = $"Saved, but the report could not be read back. {refreshed.Error} Run it again.";
                return;
            }

            report = refreshed.Value;
            editing = null;
        }
        finally
        {
            isSaving = false;
        }
    }

    /// <summary>The one thing about a day worth a word under its date, most serious first.</summary>
    private static string? FlagFor(VanDailyTruckDay day) =>
        day.NotReported ? "not reported"
        : day.DistanceWithoutSales ? "distance, no sales"
        : day.ReturnedLate ? "closed a day late"
        : !day.IsWorkingDay ? "weekend"
        : !day.IsComplete ? "today"
        : null;

    private static string Times(VanDailyTruckDay day)
    {
        if (day.TimeOut is not { } timeOut)
        {
            return "—";
        }

        // A day closed on a later date has no return time worth showing: it is when End Day was
        // tapped, not when the truck got back.
        var back = day.TimeIn is { } timeIn && !day.ReturnedLate ? timeIn.ToString("HH:mm") : "…";
        return $"{timeOut:HH:mm} — {back}";
    }

    private static string? OdometerTitle(VanDailyTruckDay day)
    {
        if (day.StartingMileage is null && day.ClosingMileage is null)
        {
            return null;
        }

        var unit = VanOdometer.ReadsMiles(day.OdometerUnit) ? "miles" : "km";
        return $"Odometer {day.StartingMileage?.ToString("N0") ?? "?"} to {day.ClosingMileage?.ToString("N0") ?? "?"} {unit}";
    }

    /// <summary>What a day's CCR was measured against, said in full on hover.</summary>
    private static string? CcrTitle(VanDailyTruckDay day)
    {
        if (day.PlannedCustomers is not > 0)
        {
            return null;
        }

        return day.PlanIsWholeList
            ? $"No round is known for this day, so this is every check-in against the truck's whole list of "
              + $"{day.PlannedCustomers:N0} shops. Enter the area worked and it is measured against that area's round."
            : $"{day.PlannedCallsMade:N0} of the {day.PlannedCustomers:N0} shops on this day's round were checked into. "
              + "The round is the shops scheduled for the weekday or called on before in this area.";
    }

    private static string? CcrTitle(VanDailyTruckSummary truck)
    {
        if (truck.PlannedCustomers <= 0)
        {
            return null;
        }

        if (truck.PlanIsWholeList)
        {
            return $"No day in the period has a round, so this is {truck.CcrCustomersVisited:N0} check-ins against "
                   + $"the whole customer list, {truck.PlannedCustomers:N0} shop-days. Enter the area worked on each "
                   + "day and it is measured against those rounds.";
        }

        return $"{truck.CcrCustomersVisited:N0} planned calls made of {truck.PlannedCustomers:N0} on the rounds worked."
               + (truck.DaysOnWholeList > 0
                   ? $" {truck.DaysOnWholeList} of its days had no round and are left out."
                   : string.Empty);
    }

    private static string Money(decimal? amount) => amount is { } value ? $"${value:N0}" : "—";

    // — Takings in other currencies ———————————————————————————————————————————————————————————

    /// <summary>
    /// What a set of days took in currencies other than US dollars, one entry per currency mix, largest
    /// first. These are in none of the dollar figures, so the page says so rather than leaving them out
    /// silently: before this they were only in the Excel export.
    /// </summary>
    private static List<(string Codes, decimal Amount, int Days)> OtherCurrencyTakings(IEnumerable<VanDailyTruckDay> days) =>
        days
            .Where(day => day.OtherCurrencySales is > 0m)
            .GroupBy(day => day.OtherCurrencyCodes ?? string.Empty)
            .Select(group => (group.Key, group.Sum(day => day.OtherCurrencySales!.Value), group.Count()))
            .OrderByDescending(entry => entry.Item2)
            .ToList();

    private static string OtherMoney(string? codes, decimal amount) =>
        string.IsNullOrWhiteSpace(codes) ? $"{amount:N0} in another currency" : $"{codes} {amount:N0}";

    /// <summary>"ZWG 12,400" or "ZWG 12,400 · ZAR 800" for a truck's days; null when it took only dollars.</summary>
    private static string? OtherCurrencyLine(IEnumerable<VanDailyTruckDay> days)
    {
        var takings = OtherCurrencyTakings(days);
        return takings.Count == 0
            ? null
            : string.Join(" · ", takings.Select(entry => OtherMoney(entry.Codes, entry.Amount)));
    }

    /// <summary>
    /// "GP $1,234 · 24.1%" for dollar sales with SAP's booked profit, "GP not in SAP yet" for sales none
    /// of which is posted, and null where nothing sold or the report read no margin.
    /// </summary>
    private string? GpLine(decimal? grossProfit, decimal? marginPercent, int costedSales, int sales)
    {
        if (report?.Margin is not { Available: true } || sales == 0)
        {
            return null;
        }

        if (grossProfit is not { } gp)
        {
            return "GP not in SAP yet";
        }

        var line = $"GP {(gp < 0 ? "\u2212" : "")}${Math.Abs(gp):N0}"
                   + (marginPercent is { } margin ? $" · {GrossProfitFormat.Percent(margin)}" : "");
        return GrossProfitFormat.PartOf(costedSales, sales) is { } part ? $"{line} ({part})" : line;
    }

    private static string Percent(double? rate) => rate is { } value ? $"{value:P0}" : "—";

    private static string Ink(bool on) => on ? "vsk-marked" : string.Empty;

    // Blazor writes a true bool attribute as an empty one and drops a false one, and ARIA reads neither.
    private static string Aria(bool on) => on ? "true" : "false";
}
