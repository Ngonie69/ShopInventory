using System.Globalization;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ShopInventory.Web.Common;
using ShopInventory.Web.Features.Reports.Queries.GetDesktopSalesAnalysis;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

/// <summary>
/// /shop-sales: the sales of the business partner set on a ShopStock account.
/// </summary>
/// <remarks>
/// <para>Every read names no partner. The API pins a ShopStock caller to the partner on its account
/// (<c>DesktopSalesReadScopeResolver.ResolveForPartnerReads</c>) and refuses another, so there is nothing
/// for this page to choose and nothing it could widen. An account with none set comes back as
/// <c>Report.SalesPartnerNotAssigned</c>, which the page draws in words.</para>
///
/// <para>The sales are the till sales made as that partner, from the API's own table. Not SAP's invoices:
/// the tills post to SAP as that same partner, so adding SAP's invoices would count each sale twice.</para>
/// </remarks>
public partial class ShopSales
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private IDesktopIntegrationService DesktopIntegration { get; set; } = default!;
    [Inject] private IReportExportService ExportService { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private ILogger<ShopSales> Logger { get; set; } = default!;

    private const int PageSize = 25;
    private const int ItemsShown = 10;

    /// <summary>The "not yet in SAP" statuses: rung up and waiting for a posting run, or refused by SAP.</summary>
    private static readonly string[] UnpostedStatuses = ["Pending", "Failed"];

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private DateTime? fromDate;
    private DateTime? toDate;
    private int dataVersion;

    private bool isLoading = true;
    private bool isLoadingList = true;
    private bool isExporting;
    private bool noPartner;
    private string? noPartnerMessage;
    private string? error;
    private string? listError;

    private DesktopSalesAnalysisResult? report;
    private DateTime? readAtUtc;
    private string? currency;
    private int? unpostedCount;

    private DesktopSalesListResponse? list;
    private int listPage = 1;
    private string search = string.Empty;
    private string shownSearch = string.Empty;

    private DesktopSalesCurrencyAnalysis? Section =>
        report?.Currencies.FirstOrDefault(section => section.Currency == currency)
        ?? report?.Currencies.FirstOrDefault();

    protected override async Task OnInitializedAsync()
    {
        using var pageReads = PageReads.Bind(PageLifetime);
        var today = VanReportPeriod.TodayCat;
        fromDate = today.AddDays(-(((int)today.DayOfWeek + 6) % 7)); // this week, from Monday
        toDate = today;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        using var pageReads = PageReads.Bind(PageLifetime);
        if (fromDate is null || toDate is null) return;
        if (toDate < fromDate) (fromDate, toDate) = (toDate, fromDate);

        var version = ++dataVersion;
        isLoading = true;
        error = null;
        listPage = 1;
        StateHasChanged();

        await Task.WhenAll(LoadAnalysisAsync(version), LoadListAsync(version), LoadUnpostedAsync(version));
    }

    private async Task LoadAnalysisAsync(int version)
    {
        try
        {
            var result = await Mediator.Send(new GetDesktopSalesAnalysisQuery(fromDate, toDate, null));
            if (version != dataVersion) return;

            if (result.IsError)
            {
                report = null;
                noPartner = result.FirstError.Code == "Report.SalesPartnerNotAssigned";
                noPartnerMessage = noPartner ? result.FirstError.Description : null;
                error = noPartner ? null : result.FirstError.Description;
                return;
            }

            noPartner = false;
            report = result.Value;
            readAtUtc = DateTime.UtcNow;
            if (report.Currencies.All(section => section.Currency != currency))
            {
                currency = report.Currencies.FirstOrDefault()?.Currency;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Could not load the partner sales analysis.");
            if (version == dataVersion) error = "The sales could not be read. Try again in a moment.";
        }
        finally
        {
            if (version == dataVersion) isLoading = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task LoadUnpostedAsync(int version)
    {
        try
        {
            var response = await DesktopIntegration.GetDesktopSalesAsync(new DesktopSalesQuery
            {
                FromDate = fromDate,
                ToDate = toDate,
                ConsolidationStatuses = UnpostedStatuses,
                PageSize = 1
            });
            if (version == dataVersion) unpostedCount = response?.TotalCount;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not count the partner's sales not yet in SAP.");
            if (version == dataVersion) unpostedCount = null;
        }
        finally
        {
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task LoadListAsync(int version)
    {
        isLoadingList = true;
        try
        {
            var (result, readError) = await DesktopIntegration.ReadDesktopSalesAsync(new DesktopSalesQuery
            {
                FromDate = fromDate,
                ToDate = toDate,
                Search = string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
                Page = listPage,
                PageSize = PageSize
            });
            if (version != dataVersion) return;

            list = result;
            listError = result is null ? readError ?? "The sales could not be listed." : null;
            shownSearch = search.Trim();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Could not list the partner's sales.");
            if (version == dataVersion) listError = "The sales could not be listed.";
        }
        finally
        {
            if (version == dataVersion) isLoadingList = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task SearchAsync()
    {
        listPage = 1;
        await LoadListAsync(dataVersion);
    }

    private async Task OnSearchKeyAsync(Microsoft.AspNetCore.Components.Web.KeyboardEventArgs args)
    {
        if (args.Key == "Enter") await SearchAsync();
    }

    private async Task PageAsync(int delta)
    {
        listPage = Math.Max(1, listPage + delta);
        await LoadListAsync(dataVersion);
    }

    private void PickCurrency(string code) => currency = code;

    private async Task ExportAsync()
    {
        if (report is null || isExporting) return;
        isExporting = true;
        error = null;
        StateHasChanged();
        try
        {
            var shown = report;
            var (sales, readError) = await ReadEverySaleAsync(shown.FromDate.Date, shown.ToDate.Date);
            if (sales is null)
            {
                error = readError;
                return;
            }

            // Off the circuit's thread: a month of sales is a few thousand rows for ClosedXML.
            var bytes = await Task.Run(() => ExportService.ExportDesktopSalesAnalysisToExcel(shown, sales));
            await JS.InvokeVoidAsync(
                "downloadFile",
                $"Sales_{report.CardCode}_{report.FromDate:yyyyMMdd}_{report.ToDate:yyyyMMdd}.xlsx",
                Convert.ToBase64String(bytes));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Could not export the partner sales.");
            error = $"The workbook could not be made: {ex.Message}";
        }
        finally
        {
            isExporting = false;
            StateHasChanged();
        }
    }

    /// <summary>Pages through for the export's Sales sheet.</summary>
    private const int ExportPageSize = 500;

    /// <summary>Twenty pages of 500: far past a month of one partner's till sales.</summary>
    private const int ExportMaxPages = 20;

    /// <summary>
    /// Every sale in the period, unsearched, for the workbook's Sales sheet; null with the reason when a page
    /// cannot be read or the period holds more than the cap, so the workbook never lists part of the sales.
    /// </summary>
    private async Task<(List<DesktopSaleDto>? Sales, string? Error)> ReadEverySaleAsync(DateTime from, DateTime to)
    {
        var sales = new List<DesktopSaleDto>();
        for (var page = 1; page <= ExportMaxPages; page++)
        {
            var (result, readError) = await DesktopIntegration.ReadDesktopSalesAsync(new DesktopSalesQuery
            {
                FromDate = from,
                ToDate = to,
                Page = page,
                PageSize = ExportPageSize
            });
            if (result is null)
            {
                Logger.LogWarning("Could not read page {Page} of the partner's sales for the export: {Error}", page, readError);
                return (null, "The sales could not be listed for the workbook. Try again in a moment.");
            }

            sales.AddRange(result.Sales);
            if (!result.HasMore) return (sales, null);
        }

        return (null, $"This period holds more than {ExportPageSize * ExportMaxPages:N0} sales. Pick a shorter period to export.");
    }

    // ---- What the page draws ------------------------------------------------------------------------

    /// <summary>The partner's name, from the analysis's own partner row; its code when there is none.</summary>
    private string PartnerName =>
        report?.Currencies.SelectMany(section => section.ByBusinessPartner)
            .FirstOrDefault(row => string.Equals(row.Key, report.CardCode, StringComparison.OrdinalIgnoreCase))?.Label
        ?? report?.CardCode
        ?? "your business partner";

    private string Money(decimal amount) => Money(amount, Section?.Currency);

    private static string Money(decimal amount, string? code)
    {
        var sign = amount < 0 ? "−" : string.Empty;
        var figure = Math.Abs(amount).ToString("N2", Invariant);
        return code?.ToUpperInvariant() switch
        {
            "USD" => $"{sign}${figure}",
            null or "" => $"{sign}{figure}",
            _ => $"{sign}{code} {figure}"
        };
    }

    private static string Count(int value) => value.ToString("N0", Invariant);

    private static string Quantity(decimal value) =>
        value == Math.Round(value) ? value.ToString("N0", Invariant) : value.ToString("N2", Invariant);

    /// <summary>"Up 6% on the 7 days before", or nothing when the period before sold nothing.</summary>
    private string? TrendNote(DesktopSalesCurrencyAnalysis section)
    {
        if (section.PreviousTotalAmount <= 0 || report is null) return null;
        var change = (section.TotalAmount - section.PreviousTotalAmount) / section.PreviousTotalAmount * 100m;
        var days = (report.ToDate.Date - report.FromDate.Date).Days + 1;
        var before = days == 1 ? "the day before" : $"the {days} days before";
        return Math.Abs(change) < 0.5m
            ? $"Level with {before}"
            : $"{(change > 0 ? "Up" : "Down")} {Math.Abs(change):0}% on {before}";
    }

    /// <summary>One bar of the chart.</summary>
    private sealed record Bar(string Label, string Title, decimal Amount, bool Labelled);

    /// <summary>
    /// Hour by hour for a single day, else day by day with the days that sold nothing kept as gaps, so the
    /// spacing between bars is time.
    /// </summary>
    private List<Bar> Bars(DesktopSalesCurrencyAnalysis section)
    {
        if (report is null) return [];

        if (report.FromDate.Date == report.ToDate.Date)
        {
            var hours = section.ByHour.Where(hour => hour.SalesCount > 0).ToList();
            if (hours.Count == 0) return [];
            var first = Math.Min(7, hours.Min(hour => hour.Hour));
            var last = Math.Max(17, hours.Max(hour => hour.Hour));
            var byHour = section.ByHour.ToDictionary(hour => hour.Hour, hour => hour.TotalAmount);
            return Enumerable.Range(first, last - first + 1)
                .Select(hour => new Bar(
                    hour.ToString("00", Invariant),
                    $"{hour:00}:00 · {Money(byHour.GetValueOrDefault(hour))}",
                    byHour.GetValueOrDefault(hour),
                    true))
                .ToList();
        }

        var byDay = section.ByDay.ToDictionary(day => day.Date.Date, day => day.TotalAmount);
        var dayCount = (report.ToDate.Date - report.FromDate.Date).Days + 1;
        var step = dayCount <= 14 ? 1 : dayCount <= 31 ? 3 : 7;
        return Enumerable.Range(0, dayCount)
            .Select(offset => report.FromDate.Date.AddDays(offset))
            .Select((day, index) => new Bar(
                dayCount <= 7 ? day.ToString("ddd", Invariant) : day.Day.ToString(Invariant),
                $"{day.ToString("ddd d MMM", Invariant)} · {Money(byDay.GetValueOrDefault(day))}",
                byDay.GetValueOrDefault(day),
                index % step == 0))
            .ToList();
    }

    private static string BarHeight(decimal amount, decimal max) =>
        max <= 0 || amount <= 0
            ? "0%"
            : $"{Math.Max(2m, Math.Round(amount / max * 100m, 1)).ToString(Invariant)}%";

    private string ChartTitle =>
        report is null ? string.Empty
        : report.FromDate.Date == report.ToDate.Date ? "Hour by hour" : "Day by day";

    private List<DesktopSalesItemRow> TopItems(DesktopSalesCurrencyAnalysis section) =>
        section.ItemGroups()
            .SelectMany(group => group.Items)
            .OrderByDescending(item => item.NetAmount)
            .Take(ItemsShown)
            .ToList();

    private static string ShareWidth(decimal share, decimal top) =>
        top <= 0 ? "0%" : $"{Math.Round(share / top * 100m, 1).ToString(Invariant)}%";

    private static string When(DesktopSaleDto sale)
    {
        // CreatedAt is UTC; the shop works in CAT.
        var at = DateTime.SpecifyKind(sale.CreatedAt, DateTimeKind.Utc).AddHours(2);
        return sale.DocDate.Date == at.Date
            ? at.ToString("ddd d MMM, HH:mm", Invariant)
            : sale.DocDate.ToString("ddd d MMM", Invariant);
    }

    private static (string Label, string Tag) SapStanding(DesktopSaleDto sale) => sale.ConsolidationStatus switch
    {
        "Consolidated" => ("In SAP", "dsh-tag-ok"),
        "Failed" => ("SAP refused it", "dsh-tag-crit"),
        "Excluded" => ("Not posted", "ssl-tag-mute"),
        _ => ("Waiting to post", "ssl-tag-warn")
    };

    private static string Payment(DesktopSaleDto sale) =>
        string.IsNullOrWhiteSpace(sale.PaymentMethod) ? "—" : sale.PaymentMethod!;

    private string ListSummary
    {
        get
        {
            if (list is null) return string.Empty;
            var first = (listPage - 1) * PageSize + 1;
            var last = first + list.Sales.Count - 1;
            var matching = shownSearch.Length > 0 ? $" matching “{shownSearch}”" : string.Empty;
            return list.TotalCount == 0
                ? $"No sales{matching}"
                : $"{Count(first)}–{Count(last)} of {Count(list.TotalCount)}{matching}";
        }
    }
}
