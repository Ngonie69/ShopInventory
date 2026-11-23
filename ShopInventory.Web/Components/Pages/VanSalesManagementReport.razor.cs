using System.Globalization;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ShopInventory.Web.Features.ReportProductGroups;
using ShopInventory.Web.Features.ReportProductGroups.Queries.GetReportProductGroups;
using ShopInventory.Web.Features.Reports.MonthOnMonth;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

public partial class VanSalesManagementReport : IDisposable
{
    [Inject] private IVanSalesReportService VanSalesReportService { get; set; } = default!;
    [Inject] private IReportExportService ExportService { get; set; } = default!;
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;

    private const string ViewQueryName = "view";
    private const string DayQueryName = "day";
    private const string WeekQueryName = "week";
    private const string GroupQueryName = "group";

    [SupplyParameterFromQuery(Name = VanReportLinks.From)] private string? FromQuery { get; set; }
    [SupplyParameterFromQuery(Name = VanReportLinks.To)] private string? ToQuery { get; set; }
    [SupplyParameterFromQuery(Name = ViewQueryName)] private string? ViewQuery { get; set; }
    [SupplyParameterFromQuery(Name = DayQueryName)] private string? DayQuery { get; set; }
    [SupplyParameterFromQuery(Name = WeekQueryName)] private string? WeekQuery { get; set; }
    [SupplyParameterFromQuery(Name = GroupQueryName)] private string? GroupQuery { get; set; }

    private readonly LatestLoad loads = new();

    private VanManagementReport? report;
    private VanDailyTruckReportResponse? truckReport;
    private VanManagementExtrasResponse? extras;

    private DateTime? fromDate;
    private DateTime? toDate;

    private string? error;
    private string? extrasError;
    private bool isLoading = true;
    private bool isExporting;

    private Section section = Section.Insights;

    /// <summary>The day the daily view shows, as yyyy-MM-dd; empty for the latest day reported.</summary>
    private string selectedDay = string.Empty;

    /// <summary>The Monday of the week the weekly view shows, as yyyy-MM-dd; empty for the latest week reported.</summary>
    private string selectedWeek = string.Empty;

    /// <summary>The product group counted, by id; empty for none.</summary>
    private string selectedGroup = string.Empty;

    private IReadOnlyList<ReportProductGroupDto> groups = [];
    private MonthOnMonthItemMaster? itemMaster;

    private enum Section
    {
        Insights,
        Overview,
        Daily,
        Weekly,
        Routes,
        Products,
        Breakages
    }

    private static readonly (Section Key, string Label)[] Sections =
    [
        (Section.Insights, "Insights"),
        (Section.Overview, "Management overview"),
        (Section.Daily, "Daily view"),
        (Section.Weekly, "Weekly sales"),
        (Section.Routes, "Route ranking"),
        (Section.Products, "Product group"),
        (Section.Breakages, "Breakages")
    ];

    /// <summary>
    /// Opens on the period a link or a refresh names, else this calendar month: the workbook was kept a
    /// month at a time, and its target is the month's.
    /// </summary>
    protected override async Task OnInitializedAsync()
    {
        var today = VanReportPeriod.TodayCat;
        var monthStart = new DateTime(today.Year, today.Month, 1);

        fromDate = VanReportPeriod.ParseDay(FromQuery) ?? monthStart;
        toDate = VanReportPeriod.ParseDay(ToQuery) ?? monthStart.AddMonths(1).AddDays(-1);
        selectedDay = VanReportPeriod.ParseDay(DayQuery)?.ToString("yyyy-MM-dd") ?? string.Empty;
        selectedWeek = VanReportPeriod.ParseDay(WeekQuery)?.ToString("yyyy-MM-dd") ?? string.Empty;

        if (Enum.TryParse<Section>(ViewQuery, ignoreCase: true, out var view))
        {
            section = view;
        }

        await LoadGroupsAsync();
        await LoadAsync();
    }

    /// <summary>
    /// The product groups the office keeps under Reports, with the item master that expands an SAP item
    /// group into its items. The 5 litre Shamiso columns of the workbook are one such group.
    /// </summary>
    private async Task LoadGroupsAsync()
    {
        try
        {
            var result = await Mediator.Send(new GetReportProductGroupsQuery(IncludeInactive: false));
            groups = result.IsError ? [] : result.Value.Where(group => group.IsActive).OrderBy(group => group.SortOrder).ThenBy(group => group.Name).ToList();

            if (result.IsError)
            {
                extrasError = $"The product groups could not be read: {result.FirstError.Description}";
            }

            if (groups.Any(group => group.Members.Any(member => member.Kind == ReportProductGroupMemberKind.ItemGroup)))
            {
                itemMaster = await Mediator.Send(new GetMonthOnMonthItemMasterQuery());
            }
        }
        catch (Exception ex)
        {
            groups = [];
            extrasError = $"The product groups could not be read: {ex.Message}";
        }

        var named = int.TryParse(GroupQuery, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            ? groups.FirstOrDefault(group => group.Id == id)
            : null;

        // The workbook counted 5 litre Shamiso, so a group named for it is the one to open on.
        var chosen = named
                     ?? groups.FirstOrDefault(group => group.Name.Contains("shamiso", StringComparison.OrdinalIgnoreCase))
                     ?? groups.FirstOrDefault(group => group.Name.Contains("5l", StringComparison.OrdinalIgnoreCase)
                                                       || group.Name.Contains("5 l", StringComparison.OrdinalIgnoreCase))
                     ?? groups.FirstOrDefault();

        selectedGroup = chosen?.Id.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
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
        StateHasChanged();

        try
        {
            var truckTask = VanSalesReportService.GetDailyTruckReportAsync(from, to, token);
            var extrasTask = VanSalesReportService.GetManagementExtrasAsync(from, to, ItemCodesOf(SelectedGroup), token);

            var truckResult = await truckTask;
            var extrasResult = await extrasTask;

            // The new period's report or nothing: never last month's figures under an error about this one.
            truckReport = truckResult.Value;
            extras = extrasResult.Value;
            error = truckResult.Error;

            if (truckReport is not null && extrasResult.Error is { } extrasFailure)
            {
                extrasError = $"The product units and breakages could not be read, so those sections are empty: {extrasFailure}";
            }
            else if (extrasResult.Error is null)
            {
                extrasError = null;
            }

            Rebuild();
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

    /// <summary>Counts another group without reading the truck report again.</summary>
    private async Task GroupChangedAsync()
    {
        if (truckReport is null)
        {
            return;
        }

        var token = loads.Begin();
        isLoading = true;
        StateHasChanged();

        try
        {
            var result = await VanSalesReportService.GetManagementExtrasAsync(
                truckReport.FromDate, truckReport.ToDate, ItemCodesOf(SelectedGroup), token);

            extras = result.Value;
            extrasError = result.Error is { } failure ? $"The product units and breakages could not be read: {failure}" : null;
            Rebuild();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
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

    private void Rebuild()
    {
        report = truckReport is null ? null : VanManagementReportBuilder.Build(truckReport, extras, SelectedGroup?.Name);

        if (report is not null)
        {
            // A day or week outside the period the link named falls back to the latest one reported.
            if (!DateTime.TryParse(selectedDay, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) || !report.Dates.Contains(day.Date))
            {
                selectedDay = string.Empty;
            }

            if (!DateTime.TryParse(selectedWeek, CultureInfo.InvariantCulture, DateTimeStyles.None, out var week) || report.Weeks.All(row => row.Start != week.Date))
            {
                selectedWeek = string.Empty;
            }

            WriteQuery();
        }
    }

    private ReportProductGroupDto? SelectedGroup =>
        int.TryParse(selectedGroup, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            ? groups.FirstOrDefault(group => group.Id == id)
            : null;

    /// <summary>A group's items: those it names, and every item in an SAP item group it names.</summary>
    private IReadOnlyCollection<string> ItemCodesOf(ReportProductGroupDto? group)
    {
        if (group is null)
        {
            return [];
        }

        var codes = group.Members
            .Where(member => member.Kind == ReportProductGroupMemberKind.Item)
            .Select(member => member.Code.Trim().ToUpperInvariant())
            .ToHashSet(StringComparer.Ordinal);

        var itemGroups = group.Members
            .Where(member => member.Kind == ReportProductGroupMemberKind.ItemGroup)
            .Select(member => int.TryParse(member.Code, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : (int?)null)
            .Where(number => number.HasValue)
            .Select(number => number!.Value)
            .ToHashSet();

        if (itemGroups.Count > 0 && itemMaster is not null)
        {
            foreach (var (code, info) in itemMaster.Items)
            {
                if (info.ItemsGroupCode is { } itemGroup && itemGroups.Contains(itemGroup))
                {
                    codes.Add(code.Trim().ToUpperInvariant());
                }
            }
        }

        return codes;
    }

    private void ShowSection(Section next)
    {
        section = next;
        WriteQuery();
    }

    private void WriteQuery()
    {
        var values = VanReportLinks.Query(truckReport?.FromDate ?? fromDate, truckReport?.ToDate ?? toDate);
        values[ViewQueryName] = section == Section.Insights ? null : section.ToString().ToLowerInvariant();
        values[DayQueryName] = string.IsNullOrEmpty(selectedDay) ? null : selectedDay;
        values[WeekQueryName] = string.IsNullOrEmpty(selectedWeek) ? null : selectedWeek;
        values[GroupQueryName] = string.IsNullOrEmpty(selectedGroup) ? null : selectedGroup;
        VanReportLinks.WriteQuery(Navigation, values);
    }

    public void Dispose() => loads.Dispose();

    /// <summary>The report on screen as the office's workbook: the period that was run, with the day and week chosen.</summary>
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
            var bytes = ExportService.ExportVanManagementReportToExcel(report, ShownDay, ShownWeek?.Start);
            var fileName = $"VanSalesManagementReport_{report.Truck.FromDate:yyyyMMdd}-{report.Truck.ToDate:yyyyMMdd}.xlsx";

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

    // — What the views show ———————————————————————————————————————————————————————————————————

    /// <summary>The day the daily view shows: the one chosen, else the latest any truck reported.</summary>
    private DateTime ShownDay =>
        DateTime.TryParse(selectedDay, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day.Date
            : report?.ReportDate ?? VanReportPeriod.TodayCat;

    private VanManagementWeek? ShownWeek =>
        report is null
            ? null
            : VanManagementReportBuilder.WeekOf(
                report,
                DateTime.TryParse(selectedWeek, CultureInfo.InvariantCulture, DateTimeStyles.None, out var week) ? week : null);

    private List<NocturneSelectOption<string>> DayOptions =>
        (report?.Dates ?? [])
            .Where(day => day <= VanReportPeriod.TodayCat)
            .OrderByDescending(day => day)
            .Select(day => new NocturneSelectOption<string>(
                day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                day.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture) + (day == report!.ReportDate ? " · latest" : string.Empty)))
            .Prepend(new NocturneSelectOption<string>(string.Empty, "Latest day reported") { IsUnset = true, RuleAfter = true })
            .ToList();

    private List<NocturneSelectOption<string>> WeekOptions =>
        (report?.Weeks ?? [])
            .Where(week => week.Start <= VanReportPeriod.TodayCat)
            .OrderByDescending(week => week.Start)
            .Select(week => new NocturneSelectOption<string>(
                week.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                $"Week of {week.Label}" + (week.HasData ? string.Empty : " · nothing reported")))
            .Prepend(new NocturneSelectOption<string>(string.Empty, "Latest week reported") { IsUnset = true, RuleAfter = true })
            .ToList();

    private List<NocturneSelectOption<string>> GroupOptions =>
        groups
            .Select(group => new NocturneSelectOption<string>(group.Id.ToString(CultureInfo.InvariantCulture), group.Name))
            .Prepend(new NocturneSelectOption<string>(string.Empty, "No product group") { RuleAfter = true })
            .ToList();

    private void DayChanged() => WriteQuery();

    private void WeekChanged() => WriteQuery();

    private string SectionCount(Section key) => report is null
        ? string.Empty
        : key switch
        {
            Section.Insights => report.Insights.Count.ToString(CultureInfo.InvariantCulture),
            Section.Overview => report.Trucks.Count.ToString(CultureInfo.InvariantCulture),
            Section.Routes => report.Routes.Ranked.Count.ToString(CultureInfo.InvariantCulture),
            Section.Breakages => report.Breakages.Lines.Count.ToString(CultureInfo.InvariantCulture),
            _ => string.Empty
        };

    // — Formatting ————————————————————————————————————————————————————————————————————————————

    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    private static string Money(decimal? amount) => amount is { } value ? value.ToString("$#,##0", Us) : "—";

    private static string Cents(decimal? amount) => amount is { } value ? value.ToString("$#,##0.00", Us) : "—";

    private static string Percent(double? rate) => rate is { } value ? value.ToString("P1", Us) : "—";

    private static string Count(int? count) => count is { } value ? value.ToString("N0", Us) : "—";

    private static string Units(decimal units) => units.ToString(units == decimal.Truncate(units) ? "N0" : "N2", Us);

    private static string Clock(DateTime? time) => time is { } value ? value.ToString("HH:mm", Us) : "—";

    /// <summary>A meter fill from a rate, clamped to the bar.</summary>
    private static string Fill(double? rate) =>
        $"{Math.Clamp((rate ?? 0) * 100, 0, 100).ToString("0.#", CultureInfo.InvariantCulture)}%";

    private static string AchievedTone(double? rate) => rate switch
    {
        null => "vs-chip-quiet",
        >= 1 => "vs-chip-good",
        >= 0.5 => "vs-chip-info",
        >= 0.35 => "vs-chip-warn",
        _ => "vs-chip-bad"
    };
}
