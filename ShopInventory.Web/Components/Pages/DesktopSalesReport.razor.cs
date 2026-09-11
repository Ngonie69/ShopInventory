using System.Globalization;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ShopInventory.Web.Data;
using ShopInventory.Web.Features.Reports.Queries.GetDesktopSalesAnalysis;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

public partial class DesktopSalesReport : IDisposable
{
    private const string PeriodToday = "today";
    private const string PeriodWeek = "week";
    private const string PeriodThirtyDays = "30";
    private const string PeriodMonth = "month";
    private const string PeriodCustom = "custom";

    private const string BreakdownShop = "shop";
    private const string BreakdownOperator = "operator";
    private const string BreakdownSource = "source";

    private static readonly (string Key, string Label)[] PeriodOptions =
    [
        (PeriodToday, "Today"),
        (PeriodWeek, "Last 7 days"),
        (PeriodThirtyDays, "Last 30 days"),
        (PeriodMonth, "This month"),
        (PeriodCustom, "Range")
    ];

    private static readonly (string Key, string Label)[] BreakdownOptions =
    [
        (BreakdownShop, "Shop"),
        (BreakdownOperator, "Operator"),
        (BreakdownSource, "Source")
    ];

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private IMasterDataCacheService MasterDataCache { get; set; } = default!;
    [Inject] private IReportExportService ExportService { get; set; } = default!;
    [Inject] private IAuditService AuditService { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private ILogger<DesktopSalesReport> Logger { get; set; } = default!;

    private DesktopSalesAnalysisResult? result;
    private string? currency;
    private List<WarehouseDto> warehouses = [];

    private string warehouse = "";
    private string period = PeriodToday;
    private DateTime? fromDate = DateTime.Today;
    private DateTime? toDate = DateTime.Today;
    private string breakdown = BreakdownShop;

    private bool isLoading;
    private bool isExporting;
    private bool hasLoggedView;
    private bool isDisposed;
    private string? error;
    private CancellationTokenSource loadCts = new();

    /// <summary>The currency section on screen: the one chosen, else the first the API lists, which is USD.</summary>
    private DesktopSalesCurrencyAnalysis? Current =>
        result?.Currencies.FirstOrDefault(section => section.Currency == currency)
        ?? result?.Currencies.FirstOrDefault();

    private bool HasSales => result is { Currencies.Count: > 0 };

    private IEnumerable<NocturneSelectOption<string>> WarehouseOptions =>
        warehouses
            .Where(w => !string.IsNullOrWhiteSpace(w.WarehouseCode))
            .Select(w => new NocturneSelectOption<string>(w.WarehouseCode!, w.WarehouseCode!, null))
            .Prepend(NocturneSelectOption.All("All shops"));

    protected override async Task OnInitializedAsync()
    {
        var warehouseList = await MasterDataCache.GetWarehousesAsync();
        warehouses = warehouseList?.Where(w => w.IsActive).ToList() ?? [];

        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        // A slow period answered after a quicker one would otherwise overwrite it.
        loadCts.Cancel();
        loadCts.Dispose();
        loadCts = new CancellationTokenSource();
        var cancellationToken = loadCts.Token;

        isLoading = true;
        error = null;

        try
        {
            var response = await Mediator.Send(
                new GetDesktopSalesAnalysisQuery(
                    fromDate,
                    toDate,
                    string.IsNullOrWhiteSpace(warehouse) ? null : warehouse),
                cancellationToken);

            if (cancellationToken.IsCancellationRequested || isDisposed)
            {
                return;
            }

            response.SwitchFirst(
                value =>
                {
                    result = value;

                    // The chosen currency survives a reload that still trades in it.
                    if (currency is null || value.Currencies.All(section => section.Currency != currency))
                    {
                        currency = value.Currencies.FirstOrDefault()?.Currency;
                    }
                },
                failure =>
                {
                    result = null;
                    error = failure.Description;
                });

            if (!response.IsError && !hasLoggedView)
            {
                hasLoggedView = true;
                await AuditService.LogAsync(AuditActions.ViewReports, "Report", nameof(DesktopSalesReport));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load the desktop sales analysis page");
            result = null;
            error = "Failed to load the desktop sales analysis.";
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested && !isDisposed)
            {
                isLoading = false;
            }
        }
    }

    private async Task SetPeriodAsync(string value)
    {
        if (period == value)
        {
            return;
        }

        period = value;
        var today = DateTime.Today;

        switch (value)
        {
            case PeriodToday:
                (fromDate, toDate) = (today, today);
                break;
            case PeriodWeek:
                (fromDate, toDate) = (today.AddDays(-6), today);
                break;
            case PeriodThirtyDays:
                (fromDate, toDate) = (today.AddDays(-29), today);
                break;
            case PeriodMonth:
                (fromDate, toDate) = (new DateTime(today.Year, today.Month, 1), today);
                break;
            default:
                // "Range" keeps whatever pair is already in the fields and reveals them.
                return;
        }

        await LoadAsync();
    }

    private async Task OnFromDateChanged(DateTime? value)
    {
        fromDate = value;
        await ReloadWhenRangeCompleteAsync();
    }

    private async Task OnToDateChanged(DateTime? value)
    {
        toDate = value;
        await ReloadWhenRangeCompleteAsync();
    }

    /// <summary>
    /// Reloads once both ends are set and in order, rather than asking the API a question it will refuse.
    /// </summary>
    private async Task ReloadWhenRangeCompleteAsync()
    {
        if (fromDate is null || toDate is null)
        {
            return;
        }

        if (fromDate > toDate)
        {
            error = "The period must start on or before the day it ends.";
            return;
        }

        await LoadAsync();
    }

    private async Task ExportAsync()
    {
        if (result is null || isExporting)
        {
            return;
        }

        isExporting = true;

        try
        {
            var bytes = ExportService.ExportDesktopSalesAnalysisToExcel(result);
            await JS.InvokeVoidAsync(
                "downloadFile",
                $"Desktop_Sales_Analysis_{result.FromDate:yyyyMMdd}_{result.ToDate:yyyyMMdd}.xlsx",
                Convert.ToBase64String(bytes));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to export the desktop sales analysis");
            error = $"Export failed: {ex.Message}";
        }
        finally
        {
            isExporting = false;
        }
    }

    // ── Breakdowns ───────────────────────────────────────────────────────

    private List<DesktopSalesBreakdownRow> BreakdownRows(DesktopSalesCurrencyAnalysis sales) => breakdown switch
    {
        BreakdownOperator => sales.ByOperator,
        BreakdownSource => sales.BySource,
        _ => sales.ByWarehouse
    };

    private string BreakdownHeading => breakdown switch
    {
        BreakdownOperator => "Operator",
        BreakdownSource => "Source",
        _ => "Shop"
    };

    /// <summary>
    /// Every hour from the first sale's to the last sale's, zeros included, so a quiet hour in the
    /// middle of the day shows as a gap rather than being closed up.
    /// </summary>
    private static List<DesktopSalesHourRow> HourSlots(DesktopSalesCurrencyAnalysis sales)
    {
        if (sales.ByHour.Count == 0)
        {
            return [];
        }

        var first = sales.ByHour.Min(hour => hour.Hour);
        var last = sales.ByHour.Max(hour => hour.Hour);

        return Enumerable.Range(first, last - first + 1)
            .Select(hour => sales.ByHour.FirstOrDefault(row => row.Hour == hour) ?? new DesktopSalesHourRow { Hour = hour })
            .ToList();
    }

    // ── Payment methods ──────────────────────────────────────────────────

    /// <summary>
    /// The colour a payment method wears, fixed by the method rather than by its rank in the period,
    /// so filtering to a shop that took no swipes does not repaint EcoCash in Swipe's colour.
    /// </summary>
    private static string TenderClass(string method) => method switch
    {
        "Cash" => "ops-dsa-t1",
        "Swipe" => "ops-dsa-t2",
        "Ecocash" => "ops-dsa-t3",
        "Innbucks" => "ops-dsa-t4",
        "Not recorded" => "ops-dsa-tnone",
        _ => "ops-dsa-t5"
    };

    private static string TenderLabel(string method) => method switch
    {
        "Ecocash" => "EcoCash",
        _ => method
    };

    private static bool NeedsReference(string method) => method is "Ecocash" or "Innbucks";

    private static decimal AmountFor(List<DesktopSalesPaymentAmount> parts, string method) =>
        parts.FirstOrDefault(part => part.PaymentMethod == method)?.TotalAmount ?? 0m;

    private string StripDescription(DesktopSalesCurrencyAnalysis sales) =>
        "Takings by payment method: " + string.Join(", ", sales.ByPaymentMethod
            .Where(row => row.TotalAmount > 0)
            .Select(row => $"{TenderLabel(row.PaymentMethod)} {Pct(row.ShareOfValuePercent)}"));

    private string HourDescription(List<DesktopSalesHourRow> slots) =>
        "Takings by hour of day: " + string.Join(", ", slots
            .Where(slot => slot.TotalAmount > 0)
            .Select(slot => $"{HourLabel(slot.Hour)} {Money(slot.TotalAmount)}"));

    // ── Text ─────────────────────────────────────────────────────────────

    private string PeriodText
    {
        get
        {
            if (result is null)
            {
                return "";
            }

            var dates = result.FromDate == result.ToDate
                ? result.FromDate.ToString("ddd dd MMM yyyy")
                : $"{result.FromDate:dd MMM} – {result.ToDate:dd MMM yyyy}";

            // The API's word on the shop, not the filter's: a shop-confined account is narrowed to its
            // own shop without choosing one.
            return result.WarehouseCode is { Length: > 0 } shop ? $"{dates} · {shop}" : $"{dates} · all shops";
        }
    }

    private string Money(decimal value) =>
        $"{Current?.Currency} {value.ToString("N2", CultureInfo.InvariantCulture)}".Trim();

    /// <summary>A figure inside a table whose currency the page already states; a zero is a dash.</summary>
    private static string Amount(decimal value) =>
        value == 0 ? "—" : value.ToString("N2", CultureInfo.InvariantCulture);

    private static string Pct(decimal percent) =>
        percent.ToString("0.#", CultureInfo.InvariantCulture) + "%";

    private static string Weight(decimal value) =>
        value.ToString(CultureInfo.InvariantCulture);

    private static string Percent(decimal part, decimal whole) =>
        whole <= 0 ? "0%" : (part / whole * 100m).ToString("0.##", CultureInfo.InvariantCulture) + "%";

    private static string Plural(int count, string noun) =>
        $"{count.ToString("N0", CultureInfo.InvariantCulture)} {noun}{(count == 1 ? "" : "s")}";

    private static string HourLabel(int hour) => $"{hour % 24:00}:00";

    public void Dispose()
    {
        isDisposed = true;
        loadCts.Cancel();
        loadCts.Dispose();
    }
}
