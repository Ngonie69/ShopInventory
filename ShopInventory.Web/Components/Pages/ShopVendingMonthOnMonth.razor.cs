using System.Globalization;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ShopInventory.Web.Features.ReportProductGroups.Queries.GetReportProductGroups;
using ShopInventory.Web.Features.Reports.MonthOnMonth;
using ShopInventory.Web.Features.Reports.Queries.GetCounterMonthOnMonth;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

/// <summary>
/// /reports/shop-vending-month-on-month: two months of shop and vending sales side by side, by product
/// group, tender and product line, with an Excel download.
/// </summary>
/// <remarks>
/// Laid out from the "Month on Month redesign" canvas: every figure is a pair of bars (earlier month grey,
/// later month accent), and the takings table switches between month totals and takings per trading day,
/// the fair comparison when a month is part-traded (the tills went live mid-September 2026).
/// </remarks>
public partial class ShopVendingMonthOnMonth
{
    [Inject] private IMediator Mediator { get; set; } = null!;
    [Inject] private IMasterDataCacheService MasterDataCache { get; set; } = null!;
    [Inject] private IReportExportService ExportService { get; set; } = null!;
    [Inject] private IJSRuntime JS { get; set; } = null!;
    [Inject] private ILogger<ShopVendingMonthOnMonth> Logger { get; set; } = null!;

    /// <summary>How far back the month lists reach.</summary>
    private const int MonthsOffered = 36;

    private NocturneSelectOption<string>[] monthOptions = [];
    private string previousMonth = string.Empty;
    private string currentMonth = string.Empty;
    private string zigRateText = string.Empty;

    private bool loading;
    private bool exporting;
    private bool needsZigRate;
    private bool perDay;
    private string? error;
    private CounterMonthOnMonthData? data;
    private CounterMonthOnMonthReport? report;

    protected override async Task OnInitializedAsync()
    {
        var thisMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        monthOptions = Enumerable.Range(0, MonthsOffered)
            .Select(i => thisMonth.AddMonths(-i))
            .Select(m => new NocturneSelectOption<string>(Key(m), m.ToString("MMMM yyyy", CultureInfo.InvariantCulture))
            {
                Hint = m == thisMonth ? "to date" : null
            })
            .ToArray();

        // The last complete month against the one before it: what a month-end review asks for.
        currentMonth = Key(thisMonth.AddMonths(-1));
        previousMonth = Key(thisMonth.AddMonths(-2));

        await RunAsync();
    }

    private static string Key(DateTime month) => month.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private static DateTime Parse(string key) => DateTime.ParseExact(key, "yyyy-MM", CultureInfo.InvariantCulture);

    private static string MonthName(string key) => Parse(key).ToString("MMMM yyyy", CultureInfo.InvariantCulture);

    private bool SameMonth => previousMonth == currentMonth;

    /// <summary>True when any place traded only part of a month, which is when per-day figures matter.</summary>
    private bool HasPartMonth => report?.Notes.Any(n => n.Contains("covers only part", StringComparison.Ordinal)) == true;

    private async Task RunAsync()
    {
        if (SameMonth)
        {
            error = null;
            report = null;
            return;
        }

        loading = true;
        error = null;
        try
        {
            var result = await Mediator.Send(new GetCounterMonthOnMonthQuery(Parse(previousMonth), Parse(currentMonth)));
            if (result.IsError)
            {
                error = result.FirstError.Description;
                data = null;
                report = null;
                return;
            }

            data = result.Value;
            needsZigRate = CounterMonthOnMonthReportBuilder.NeedsZigRate(data);
            await BuildAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Month-on-month report failed for {Previous} vs {Current}", previousMonth, currentMonth);
            error = "The report could not be built. Try again, or check the API is running.";
        }
        finally
        {
            loading = false;
        }
    }

    /// <summary>Applies product groups, the product master and the ZiG rate to the figures already read.</summary>
    private async Task BuildAsync()
    {
        if (data is null)
        {
            return;
        }

        var groups = await Mediator.Send(new GetReportProductGroupsQuery(IncludeInactive: false));
        if (groups.IsError)
        {
            error = groups.FirstError.Description;
            return;
        }

        var master = await Mediator.Send(new GetMonthOnMonthItemMasterQuery());
        var warehouses = await MasterDataCache.GetWarehousesAsync();

        var warehouseNames = warehouses
            .Where(w => !string.IsNullOrWhiteSpace(w.WarehouseCode))
            .GroupBy(w => w.WarehouseCode!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().WarehouseName ?? g.Key, StringComparer.OrdinalIgnoreCase);

        report = CounterMonthOnMonthReportBuilder.Build(
            data, groups.Value, master.Items, master.ItemGroupNames, warehouseNames, ZigRate, DateTime.Today);
    }

    private decimal? ZigRate =>
        decimal.TryParse(zigRateText, NumberStyles.Number, CultureInfo.InvariantCulture, out var rate) && rate > 0 ? rate : null;

    private async Task ApplyZigRateAsync()
    {
        if (ZigRate is null)
        {
            error = "Enter how many ZiG make one US dollar, e.g. 30.";
            return;
        }

        error = null;
        await BuildAsync();
    }

    private async Task DownloadAsync()
    {
        if (report is null)
        {
            return;
        }

        exporting = true;
        try
        {
            var built = report;
            var bytes = await Task.Run(() => ExportService.ExportCounterMonthOnMonthToExcel(built));
            await JS.InvokeVoidAsync(
                "downloadFile",
                $"Shop-Vending-{built.CurrentMonth:yyyy-MM}-vs-{built.PreviousMonth:yyyy-MM}.xlsx",
                Convert.ToBase64String(bytes));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Month-on-month workbook failed");
            error = "The workbook could not be written.";
        }
        finally
        {
            exporting = false;
        }
    }

    // — What the takings table shows —

    /// <summary>A place's net takings as the table shows them: the month, or per trading day.</summary>
    private MonthPair Shown(MonthOnMonthPlace place) => perDay
        ? new MonthPair(
            place.PreviousTradingDays == 0 ? 0 : place.NetTakings.Previous / place.PreviousTradingDays,
            place.CurrentTradingDays == 0 ? 0 : place.NetTakings.Current / place.CurrentTradingDays)
        : place.NetTakings;

    private string ShownMoney(decimal value) => perDay ? "$" + value.ToString("#,##0", CultureInfo.InvariantCulture) : "$" + Money(value);

    // — Formatting for the page —

    private static string Money(decimal value) => value.ToString("#,##0.00", CultureInfo.InvariantCulture);

    private static string Whole(decimal value) => "$" + value.ToString("#,##0", CultureInfo.InvariantCulture);

    private static string Pct(decimal fraction) => fraction.ToString("0.0%", CultureInfo.InvariantCulture);

    private static string Change(MonthPair pair) =>
        pair.ChangeFraction is { } f ? (f >= 0 ? "+" : "−") + Math.Abs(f).ToString("0.0%", CultureInfo.InvariantCulture) : pair.IsNew ? "new" : "–";

    /// <summary>The pill's tone: up, down or level (none to compare).</summary>
    private static string Tone(MonthPair pair) =>
        pair.ChangeFraction is { } f ? f > 0.0005m ? "is-up" : f < -0.0005m ? "is-down" : "is-flat" : pair.IsNew ? "is-up" : "is-flat";

    /// <summary>A bar's width, as a share of the largest value it is drawn against.</summary>
    private static string Width(decimal value, decimal max) =>
        max <= 0 || value <= 0
            ? "0%"
            : Math.Max(1m, Math.Round(value / max * 100m, 1)).ToString("0.#", CultureInfo.InvariantCulture) + "%";

    private static string Percent(decimal fraction) =>
        Math.Round(Math.Clamp(fraction, 0, 1) * 100m, 2).ToString("0.##", CultureInfo.InvariantCulture) + "%";

    private static decimal Max(MonthPair pair) => Math.Max(pair.Previous, pair.Current);
}
