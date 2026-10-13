using Microsoft.AspNetCore.Components;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

public partial class VanDailyTruckReport
{
    [Inject] private IVanSalesReportService VanSalesReportService { get; set; } = default!;

    private VanDailyTruckReportResponse? report;

    private DateTime? fromDate;
    private DateTime? toDate;

    private int? openRoute;
    private string? error;
    private bool isLoading = true;
    private bool isSaving;

    // The one day whose office entry is open, and what has been typed into it.
    private VanDailyTruckDay? editing;
    private string? entryArea;
    private decimal? entryFuel;
    private decimal? entryAccount;
    private string? entryNotes;
    private string? entryError;

    protected override async Task OnInitializedAsync()
    {
        // The month so far: the period the workbook this replaces was kept for.
        var today = IAuditService.ToCAT(DateTime.UtcNow).Date;
        fromDate = new DateTime(today.Year, today.Month, 1);
        toDate = today;

        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (fromDate is not { } from || toDate is not { } to)
        {
            error = "Choose both dates.";
            return;
        }

        isLoading = true;
        error = null;
        CancelEntry();
        StateHasChanged();

        try
        {
            report = await VanSalesReportService.GetDailyTruckReportAsync(from, to);

            if (report is null)
            {
                error = "The report could not be loaded. Check the period is 93 days or fewer and that the API is reachable.";
            }
        }
        finally
        {
            isLoading = false;
            StateHasChanged();
        }
    }

    private IEnumerable<VanDailyTruckDay> DaysOf(int routeId) =>
        (report?.Days ?? []).Where(day => day.RouteId == routeId).OrderBy(day => day.TradingDate);

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

            if (refreshed is null)
            {
                entryError = "Saved, but the report could not be read back. Run it again.";
                return;
            }

            report = refreshed;
            editing = null;
        }
        finally
        {
            isSaving = false;
        }
    }

    /// <summary>The fleet has sold less than its target for the working days so far.</summary>
    private bool BehindPace =>
        report?.Total is { TargetToDate: { } paced } total && total.Sales < paced;

    private string PaceNote
    {
        get
        {
            if (report?.Total is not { TargetToDate: { } target } total)
            {
                return "no targets set";
            }

            var gap = total.Sales - target;
            return gap >= 0
                ? $"{Money(gap)} ahead of the days so far"
                : $"{Money(-gap)} behind the days so far";
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

    private static string Percent(double? rate) => rate is { } value ? $"{value:P0}" : "—";

    private static string Ink(bool on) => on ? "vsc-flagged" : string.Empty;

    private static string ChipInk(int count) => count > 0 ? "vsk-chip-on" : string.Empty;
}
