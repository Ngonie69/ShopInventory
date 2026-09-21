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
    private const string BreakdownPartner = "partner";
    private const string BreakdownOperator = "operator";
    private const string BreakdownSource = "source";

    private const string MeasureTakings = "takings";
    private const string MeasureSales = "sales";

    private const string SortTakings = "takings";
    private const string SortSales = "sales";
    private const string SortName = "name";

    /// <summary>How many best sellers show before the rest are asked for.</summary>
    private const int ItemsShown = 8;

    /// <summary>A day this many times the period's daily average is called out.</summary>
    private const decimal StandoutDayFactor = 2.5m;

    private static readonly (string Key, string Label)[] PeriodOptions =
    [
        (PeriodToday, "Today"),
        (PeriodWeek, "7 days"),
        (PeriodThirtyDays, "30 days"),
        (PeriodMonth, "This month"),
        (PeriodCustom, "Range")
    ];

    private static readonly (string Key, string Label)[] BreakdownOptions =
    [
        (BreakdownShop, "Shop"),
        (BreakdownPartner, "Business partner"),
        (BreakdownOperator, "Operator"),
        (BreakdownSource, "Source")
    ];

    private static readonly (string Key, string Label)[] MeasureOptions =
    [
        (MeasureTakings, "Takings"),
        (MeasureSales, "Sales")
    ];

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private IMasterDataCacheService MasterDataCache { get; set; } = default!;
    [Inject] private IReportExportService ExportService { get; set; } = default!;
    [Inject] private IAuditService AuditService { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private ILogger<DesktopSalesReport> Logger { get; set; } = default!;

    /// <summary>
    /// Every analysis this period and shop have asked for, so moving between days and payment methods and
    /// back again does not ask the API twice. Emptied whenever the period or shop changes, and on refresh.
    /// </summary>
    private readonly Dictionary<AnalysisKey, DesktopSalesAnalysisResult> loaded = [];

    /// <summary>The three analyses on screen; kept while the next ones load so the page does not blank.</summary>
    private Shown? shown;

    private List<WarehouseDto> warehouses = [];

    private string warehouse = "";
    private string period = PeriodThirtyDays;
    private DateTime? fromDate = DateTime.Today.AddDays(-29);
    private DateTime? toDate = DateTime.Today;
    private string breakdown = BreakdownShop;
    private string measure = MeasureTakings;
    private string sort = SortTakings;
    private string? currency;

    /// <summary>The payment method the page is confined to, by its reporting name.</summary>
    private string? method;

    /// <summary>The one day the page is confined to, picked from the trading pattern.</summary>
    private DateTime? day;

    private bool showAllItems;
    private bool isLoading;
    private bool isExporting;
    private bool hasLoggedView;
    private bool isDisposed;
    private string? error;
    private string? toast;
    private CancellationTokenSource loadCts = new();
    private CancellationTokenSource toastCts = new();

    /// <summary>
    /// One analysis request, by what it is confined to.
    /// </summary>
    private readonly record struct AnalysisKey(DateTime From, DateTime To, string Warehouse, string? Method);

    /// <summary>
    /// The three answers the page draws from.
    /// </summary>
    /// <param name="Context">The whole period across every payment method: the trading pattern and the alerts.</param>
    /// <param name="Focus">The period or picked day, confined to the picked method: every other figure.</param>
    /// <param name="Payments">The period or picked day across every method: how it was paid, so the other
    /// methods stay in view to be picked instead.</param>
    /// <param name="Day">The day these were loaded for, which the page's own field may already have moved on from.</param>
    /// <param name="Method">The payment method these were loaded for, likewise.</param>
    private sealed record Shown(
        DesktopSalesAnalysisResult Context,
        DesktopSalesAnalysisResult Focus,
        DesktopSalesAnalysisResult Payments,
        DateTime? Day,
        string? Method);

    private IEnumerable<NocturneSelectOption<string>> WarehouseOptions =>
        warehouses
            .Where(w => !string.IsNullOrWhiteSpace(w.WarehouseCode))
            .Select(w => new NocturneSelectOption<string>(
                w.WarehouseCode!,
                string.IsNullOrWhiteSpace(w.WarehouseName) ? w.WarehouseCode! : w.WarehouseName!)
            {
                Hint = string.IsNullOrWhiteSpace(w.WarehouseName) ? null : w.WarehouseCode
            })
            .Prepend(NocturneSelectOption.All("All shops"));

    protected override async Task OnInitializedAsync()
    {
        var warehouseList = await MasterDataCache.GetWarehousesAsync();
        warehouses = warehouseList?.Where(w => w.IsActive).ToList() ?? [];

        await LoadAsync();
    }

    // ── Loading ──────────────────────────────────────────────────────────

    private async Task LoadAsync()
    {
        if (fromDate is not { } from || toDate is not { } to)
        {
            return;
        }

        // A slow answer arriving after a quicker one would otherwise overwrite it.
        loadCts.Cancel();
        loadCts.Dispose();
        loadCts = new CancellationTokenSource();
        var cancellationToken = loadCts.Token;

        var focusFrom = day ?? from.Date;
        var focusTo = day ?? to.Date;
        var contextKey = new AnalysisKey(from.Date, to.Date, warehouse, null);
        var focusKey = new AnalysisKey(focusFrom, focusTo, warehouse, method);
        var paymentsKey = new AnalysisKey(focusFrom, focusTo, warehouse, null);

        isLoading = true;
        error = null;

        try
        {
            // One after another rather than together: they share the circuit's HttpClient handlers, and at
            // most three are ever missing.
            foreach (var key in new[] { contextKey, focusKey, paymentsKey }.Distinct())
            {
                if (loaded.ContainsKey(key))
                {
                    continue;
                }

                var response = await Mediator.Send(
                    new GetDesktopSalesAnalysisQuery(
                        key.From,
                        key.To,
                        key.Warehouse == "" ? null : key.Warehouse,
                        key.Method),
                    cancellationToken);

                if (cancellationToken.IsCancellationRequested || isDisposed)
                {
                    return;
                }

                if (response.IsError)
                {
                    error = response.FirstError.Description;
                    shown = null;
                    return;
                }

                loaded[key] = response.Value;
            }

            shown = new Shown(loaded[contextKey], loaded[focusKey], loaded[paymentsKey], day, method);

            // The chosen currency survives a reload that still trades in it.
            if (currency is null || shown.Context.Currencies.All(section => section.Currency != currency))
            {
                currency = shown.Context.Currencies.FirstOrDefault()?.Currency;
            }

            if (!hasLoggedView)
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
            shown = null;
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

    /// <summary>A different period or shop: nothing already loaded answers it, and a picked day may not be in it.</summary>
    private Task ReloadScopeAsync()
    {
        loaded.Clear();
        day = null;
        return LoadAsync();
    }

    private async Task RefreshAsync()
    {
        loaded.Clear();
        await LoadAsync();

        if (error is null)
        {
            ShowToast("Figures refreshed");
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

        await ReloadScopeAsync();
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

        await ReloadScopeAsync();
    }

    private Task SetDayAsync(DateTime? value)
    {
        day = day == value ? null : value;
        return LoadAsync();
    }

    private Task SetMethodAsync(string? value)
    {
        method = method == value ? null : value;
        return LoadAsync();
    }

    private Task ClearShopAsync()
    {
        warehouse = "";
        return ReloadScopeAsync();
    }

    private Task ClearAllAsync()
    {
        var scopeChanged = warehouse != "";
        warehouse = "";
        method = null;
        day = null;

        if (scopeChanged)
        {
            loaded.Clear();
        }

        return LoadAsync();
    }

    private async Task ExportAsync()
    {
        if (shown is null || isExporting)
        {
            return;
        }

        isExporting = true;

        try
        {
            // The figures on screen: the picked day and method, not the whole period.
            var report = shown.Focus;
            var bytes = ExportService.ExportDesktopSalesAnalysisToExcel(report);
            await JS.InvokeVoidAsync(
                "downloadFile",
                $"Desktop_Sales_Analysis_{report.FromDate:yyyyMMdd}_{report.ToDate:yyyyMMdd}.xlsx",
                Convert.ToBase64String(bytes));

            ShowToast("Workbook downloaded");
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

    private void ShowToast(string message)
    {
        toastCts.Cancel();
        toastCts.Dispose();
        toastCts = new CancellationTokenSource();
        var token = toastCts.Token;

        toast = message;

        _ = Task.Delay(2400, token).ContinueWith(
            _ => InvokeAsync(() =>
            {
                toast = null;
                StateHasChanged();
            }),
            token,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
    }

    // ── Sections ─────────────────────────────────────────────────────────

    /// <summary>The currency on screen: the one chosen, else the first the period traded in, which is USD.</summary>
    private string? Currency =>
        shown?.Context.Currencies.FirstOrDefault(section => section.Currency == currency)?.Currency
        ?? shown?.Context.Currencies.FirstOrDefault()?.Currency;

    /// <summary>
    /// A result's section in the currency on screen, or an empty one: a day or method with no sales still
    /// draws its zeros, with the payment methods the result lists.
    /// </summary>
    private DesktopSalesCurrencyAnalysis Section(DesktopSalesAnalysisResult result) =>
        result.Currencies.FirstOrDefault(section => section.Currency == Currency)
        ?? new DesktopSalesCurrencyAnalysis
        {
            Currency = Currency ?? "",
            ByPaymentMethod = result.PaymentMethods
                .Select(paymentMethod => new DesktopSalesPaymentMethodRow { PaymentMethod = paymentMethod })
                .ToList()
        };

    // ── The trading pattern ──────────────────────────────────────────────

    private sealed record DayColumn(
        DateTime Date,
        int SalesCount,
        decimal TotalAmount,
        decimal Value,
        List<(string Method, decimal Value)> Parts);

    /// <summary>
    /// Every day of the period, zeros included, confined to the picked method but never to the picked day —
    /// so the pattern stays in view to move between days.
    /// </summary>
    private List<DayColumn> DayColumns(DesktopSalesAnalysisResult context)
    {
        var section = Section(context);
        var columns = new List<DayColumn>();

        for (var date = context.FromDate.Date; date <= context.ToDate.Date; date = date.AddDays(1))
        {
            var row = section.ByDay.FirstOrDefault(d => d.Date.Date == date);
            var parts = (row?.ByPaymentMethod ?? [])
                .Where(part => method is null || part.PaymentMethod == method)
                .ToList();

            var salesCount = parts.Sum(part => part.SalesCount);
            var total = parts.Sum(part => part.TotalAmount);

            columns.Add(new DayColumn(
                date,
                salesCount,
                total,
                measure == MeasureSales ? salesCount : total,
                parts
                    .Select(part => (part.PaymentMethod, measure == MeasureSales ? (decimal)part.SalesCount : part.TotalAmount))
                    .Where(part => part.Item2 > 0)
                    .ToList()));
        }

        return columns;
    }

    /// <summary>Every nth day is labelled, so a year of columns does not print 366 overlapping numbers.</summary>
    private static int LabelStep(int days) => days switch
    {
        > 180 => 30,
        > 62 => 7,
        > 20 => 3,
        > 10 => 2,
        _ => 1
    };

    private string ColumnReadoutTitle(DayColumn column) =>
        measure == MeasureSales ? Plural(column.SalesCount, "sale") : Money(column.TotalAmount);

    private string ColumnReadoutSub(DayColumn column) =>
        $"{DayLabel(column.Date)} · " + (measure == MeasureSales ? Money(column.TotalAmount) : Plural(column.SalesCount, "sale"));

    private string ColumnTitle(DayColumn column) =>
        $"{DayLabel(column.Date)} · {Plural(column.SalesCount, "sale")} · {Money(column.TotalAmount)}";

    /// <summary>
    /// Every hour from seven to six, widened to take in any sale outside them, zeros included — so a
    /// quiet hour in the middle of the day shows as a gap rather than being closed up.
    /// </summary>
    private static List<DesktopSalesHourRow> HourSlots(DesktopSalesCurrencyAnalysis sales)
    {
        var first = Math.Min(7, sales.ByHour.Count == 0 ? 7 : sales.ByHour.Min(hour => hour.Hour));
        var last = Math.Max(18, sales.ByHour.Count == 0 ? 18 : sales.ByHour.Max(hour => hour.Hour));

        return Enumerable.Range(first, last - first + 1)
            .Select(hour => sales.ByHour.FirstOrDefault(row => row.Hour == hour) ?? new DesktopSalesHourRow { Hour = hour })
            .ToList();
    }

    // ── Headline and alerts ──────────────────────────────────────────────

    /// <summary>
    /// The change on the same number of days just before, or null where there were none before — a rise
    /// from nothing is not a percentage.
    /// </summary>
    private static decimal? ChangePercent(DesktopSalesCurrencyAnalysis sales) =>
        sales.PreviousTotalAmount > 0
            ? Math.Round((sales.TotalAmount - sales.PreviousTotalAmount) / sales.PreviousTotalAmount * 100m, 1)
            : null;

    private static string PreviousBasis(DesktopSalesAnalysisResult result)
    {
        var days = (result.ToDate.Date - result.FromDate.Date).Days + 1;
        return days == 1 ? "day" : $"{days} days";
    }

    private sealed record Alert(string Icon, string Text, string Action, Func<Task> Act);

    /// <summary>
    /// What is worth a look: wallet sales that cannot be tied to money arriving, and a day far above the
    /// period's usual.
    /// </summary>
    private List<Alert> Alerts(Shown view, List<DayColumn> columns)
    {
        var alerts = new List<Alert>();

        foreach (var row in Section(view.Payments).ByPaymentMethod)
        {
            if (NeedsReference(row.PaymentMethod) && row.WithoutReferenceCount > 0 && view.Method != row.PaymentMethod)
            {
                var paymentMethod = row.PaymentMethod;
                alerts.Add(new Alert(
                    "ph-warning",
                    $"{Plural(row.WithoutReferenceCount, $"{TenderLabel(paymentMethod)} sale")} settled without a payment reference",
                    $"Show {TenderLabel(paymentMethod)}",
                    () => SetMethodAsync(paymentMethod)));
            }
        }

        var traded = columns.Where(column => column.TotalAmount > 0).ToList();
        if (traded.Count > 1)
        {
            var average = traded.Sum(column => column.TotalAmount) / traded.Count;
            var busiest = traded.MaxBy(column => column.TotalAmount)!;

            if (busiest.TotalAmount > average * StandoutDayFactor && view.Day != busiest.Date)
            {
                alerts.Add(new Alert(
                    "ph-trend-up",
                    $"{DayLabel(busiest.Date)} took {Money(busiest.TotalAmount)} — well above the daily average",
                    "Open that day",
                    () => SetDayAsync(busiest.Date)));
            }
        }

        return alerts;
    }

    // ── Who took it ──────────────────────────────────────────────────────

    private List<DesktopSalesBreakdownRow> BreakdownRows(DesktopSalesCurrencyAnalysis sales)
    {
        var rows = breakdown switch
        {
            BreakdownPartner => sales.ByBusinessPartner,
            BreakdownOperator => sales.ByOperator,
            BreakdownSource => sales.BySource,
            _ => sales.ByWarehouse
        };

        return sort switch
        {
            SortName => rows.OrderBy(BreakdownLabel, StringComparer.OrdinalIgnoreCase).ToList(),
            SortSales => rows.OrderByDescending(row => row.SalesCount).ThenByDescending(row => row.TotalAmount).ToList(),
            _ => rows.OrderByDescending(row => row.TotalAmount).ThenBy(BreakdownLabel, StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    private string BreakdownHeading => breakdown switch
    {
        BreakdownPartner => "Business partner",
        BreakdownOperator => "Operator",
        BreakdownSource => "Source",
        _ => "Shop"
    };

    /// <summary>
    /// A shop by its code, which is what the tills and the sale list call it; a business partner by the
    /// name its sales carried, with the code beneath; anything else by name.
    /// </summary>
    private string BreakdownLabel(DesktopSalesBreakdownRow row) =>
        breakdown == BreakdownShop && row.Key != "" ? row.Key : row.Label;

    private string? BreakdownSub(DesktopSalesBreakdownRow row) => breakdown switch
    {
        BreakdownShop => WarehouseName(row.Key),
        // The code whenever it is not the name itself: "POSETA" under "Poseta" is the code SAP is searched by.
        BreakdownPartner => row.Key != "" && row.Key != row.Label ? row.Key : null,
        BreakdownSource => row.Key != "" && row.Key != row.Label ? row.Key : null,
        _ => row.SalesCount == 0 ? null : $"avg {Money(row.TotalAmount / row.SalesCount)}"
    };

    private string? WarehouseName(string code) =>
        warehouses.FirstOrDefault(w => string.Equals(w.WarehouseCode, code, StringComparison.OrdinalIgnoreCase))?.WarehouseName;

    private string SortState(string key) => sort == key ? "descending" : "none";

    // ── Chips ────────────────────────────────────────────────────────────

    private string RangeText(DesktopSalesAnalysisResult context)
    {
        var dates = context.FromDate.Date == context.ToDate.Date
            ? context.FromDate.ToString("ddd dd MMM yyyy", CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"{context.FromDate:dd MMM yyyy} – {context.ToDate:dd MMM yyyy}");

        // The API's word on the shop, not the filter's: a shop-confined account is narrowed to its own
        // shop without choosing one.
        return context.WarehouseCode is { Length: > 0 } shop ? $"{dates} · {shop}" : $"{dates} · all shops";
    }

    private string ShopChip => WarehouseName(warehouse) is { Length: > 0 } name ? name : warehouse;

    private bool HasChips => warehouse != "" || method != null || day != null;

    // ── Payment methods ──────────────────────────────────────────────────

    /// <summary>
    /// The colour a payment method wears, fixed by the method rather than by its rank in the period,
    /// so filtering to a shop that took no swipes does not repaint EcoCash in Swipe's colour.
    /// </summary>
    private static string TenderClass(string paymentMethod) => paymentMethod switch
    {
        "Cash" => "ops-dsa-t1",
        "Swipe" => "ops-dsa-t2",
        "Ecocash" => "ops-dsa-t3",
        "Innbucks" => "ops-dsa-t4",
        "Not recorded" => "ops-dsa-tnone",
        _ => "ops-dsa-t5"
    };

    private static string TenderLabel(string paymentMethod) => paymentMethod switch
    {
        "Ecocash" => "EcoCash",
        _ => paymentMethod
    };

    private static bool NeedsReference(string paymentMethod) => paymentMethod is "Ecocash" or "Innbucks";

    private static decimal AmountFor(List<DesktopSalesPaymentAmount> parts, string paymentMethod) =>
        parts.FirstOrDefault(part => part.PaymentMethod == paymentMethod)?.TotalAmount ?? 0m;

    private string MethodSub(DesktopSalesPaymentMethodRow row)
    {
        var text = Plural(row.SalesCount, "sale");

        if (row.SalesCount > 0)
        {
            text += $" · avg {Money(row.AverageSale)}";
        }

        return text;
    }

    private string StripDescription(DesktopSalesCurrencyAnalysis sales) =>
        "Takings by payment method: " + string.Join(", ", sales.ByPaymentMethod
            .Where(row => row.TotalAmount > 0)
            .Select(row => $"{TenderLabel(row.PaymentMethod)} {Pct(row.ShareOfValuePercent)}"));

    private string HourDescription(List<DesktopSalesHourRow> slots) =>
        "Takings by hour of day: " + string.Join(", ", slots
            .Where(slot => slot.TotalAmount > 0)
            .Select(slot => $"{HourLabel(slot.Hour)} {Money(slot.TotalAmount)}"));

    // ── Text ─────────────────────────────────────────────────────────────

    private string Money(decimal value) =>
        $"{Currency} {value.ToString("N2", CultureInfo.InvariantCulture)}".Trim();

    private static string Pct(decimal percent) =>
        percent.ToString("0.#", CultureInfo.InvariantCulture) + "%";

    private static string Weight(decimal value) =>
        value.ToString(CultureInfo.InvariantCulture);

    private static string Percent(decimal part, decimal whole) =>
        whole <= 0 ? "0%" : (part / whole * 100m).ToString("0.##", CultureInfo.InvariantCulture) + "%";

    private static string Plural(int count, string noun) =>
        $"{count.ToString("N0", CultureInfo.InvariantCulture)} {noun}{(count == 1 ? "" : "s")}";

    private static string HourLabel(int hour) => $"{hour % 24:00}:00";

    private static string DayLabel(DateTime date) => date.ToString("ddd dd MMM", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        isDisposed = true;
        loadCts.Cancel();
        loadCts.Dispose();
        toastCts.Cancel();
        toastCts.Dispose();
    }
}
