using System.Security.Claims;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using ShopInventory.Web.Features.Reports.Queries.GetReportsOverview;
using ShopInventory.Web.Services;
using static ShopInventory.Web.Features.Reports.Queries.GetReportsOverview.ReportsHubFormat;

namespace ShopInventory.Web.Components;

/// <summary>
/// The Reports Hub: the period's headline figures, what needs attention, the activity behind them
/// and a directory of every report. It opens loaded; the three reads run side by side and each part
/// of the page fills in as its read answers, so a slow stock read never holds up the sales figures.
/// </summary>
public partial class ReportsHubOverview : IDisposable
{
    /// <summary>Above this many days the chart draws weeks rather than days, so the bars stay readable.</summary>
    private const int MaxDailyBars = 62;

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private IReportExportService ExportService { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private ILogger<ReportsHubOverview> Logger { get; set; } = default!;

    [CascadingParameter] private Task<AuthenticationState>? AuthState { get; set; }

    /// <summary>The period the address names; /reports works it out from the query string.</summary>
    [Parameter, EditorRequired] public ReportsPeriod Period { get; set; } = default!;

    /// <summary>Today in CAT, the business's day.</summary>
    [Parameter, EditorRequired] public DateOnly Today { get; set; }

    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? periodLoad;
    private ReportsPeriod? loadedPeriod;
    private ClaimsPrincipal user = new();

    private ReportsHeadlineResult? headline;
    private string? headlineError;
    private bool headlineLoading;

    private ReportsActivityResult? activity;
    private string? activityError;
    private bool activityLoading;

    private ReportsStockWatchResult? stock;
    private string? stockError;
    private bool stockLoading;
    private bool stockRequested;

    private DateTime? loadedAtCat;
    private string search = string.Empty;
    private bool customOpen;
    private DateTime? customFrom;
    private DateTime? customTo;
    private bool exporting;
    private string? exportError;

    private static readonly (ReportsPeriodPreset Preset, string Label)[] Presets =
    [
        (ReportsPeriodPreset.Today, "Today"),
        (ReportsPeriodPreset.Yesterday, "Yesterday"),
        (ReportsPeriodPreset.ThisWeek, "This week"),
        (ReportsPeriodPreset.ThisMonth, "This month"),
        (ReportsPeriodPreset.Last30Days, "Last 30 days"),
        (ReportsPeriodPreset.Custom, "Pick dates"),
    ];

    protected override async Task OnInitializedAsync()
    {
        if (AuthState is not null)
        {
            user = (await AuthState).User;
        }
    }

    protected override void OnParametersSet()
    {
        if (loadedPeriod == Period)
        {
            return;
        }

        loadedPeriod = Period;
        customFrom = Period.From.ToDateTime(TimeOnly.MinValue);
        customTo = Period.To.ToDateTime(TimeOnly.MinValue);
        customOpen = Period.Preset == ReportsPeriodPreset.Custom && customOpen;
        LoadPeriod();

        if (!stockRequested)
        {
            // Stock is today's, whatever the period, so it is read once per visit.
            stockRequested = true;
            _ = LoadStockAsync(lifetime.Token);
        }
    }

    private void LoadPeriod()
    {
        periodLoad?.Cancel();
        periodLoad?.Dispose();
        periodLoad = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = periodLoad.Token;

        headline = null;
        activity = null;
        headlineError = null;
        activityError = null;
        exportError = null;
        _ = LoadHeadlineAsync(Period, token);
        _ = LoadActivityAsync(Period, token);
    }

    private async Task LoadHeadlineAsync(ReportsPeriod period, CancellationToken token)
    {
        headlineLoading = true;
        try
        {
            var result = await Mediator.Send(new GetReportsHeadlineQuery(period), token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            if (result.IsError)
            {
                headlineError = result.FirstError.Description;
            }
            else
            {
                headline = result.Value;
                loadedAtCat = IAuditService.ToCAT(DateTime.UtcNow);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Reports hub headline failed for {From} to {To}", period.From, period.To);
            headlineError = "The headline figures could not be loaded. Try again in a minute.";
        }

        headlineLoading = false;
        await InvokeAsync(StateHasChanged);
    }

    private async Task LoadActivityAsync(ReportsPeriod period, CancellationToken token)
    {
        activityLoading = true;
        try
        {
            var result = await Mediator.Send(new GetReportsActivityQuery(period), token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            if (result.IsError)
            {
                activityError = result.FirstError.Description;
            }
            else
            {
                activity = result.Value;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Reports hub activity failed for {From} to {To}", period.From, period.To);
            activityError = "This period's sales could not be loaded. Try again in a minute.";
        }

        activityLoading = false;
        await InvokeAsync(StateHasChanged);
    }

    private async Task LoadStockAsync(CancellationToken token)
    {
        stockLoading = true;
        stockError = null;
        try
        {
            var result = await Mediator.Send(new GetReportsStockWatchQuery(Today), token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            if (result.IsError)
            {
                stockError = result.FirstError.Description;
            }
            else
            {
                stock = result.Value;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Reports hub stock watch failed");
            stockError = "Stock could not be checked. Try again in a minute.";
        }

        stockLoading = false;
        await InvokeAsync(StateHasChanged);
    }

    private void Retry()
    {
        LoadPeriod();
        if (stockError is not null || (stock?.Unavailable.Count ?? 0) > 0)
        {
            _ = LoadStockAsync(lifetime.Token);
        }
    }

    // ── Period bar ─────────────────────────────────────────────────────

    private void ChoosePreset(ReportsPeriodPreset preset)
    {
        if (preset == ReportsPeriodPreset.Custom)
        {
            customOpen = !customOpen;
            return;
        }

        customOpen = false;
        Nav.NavigateTo(ReportsHubLinks.Hub(ReportsPeriod.Resolve(preset, Today)));
    }

    private void ApplyCustom()
    {
        var from = customFrom is { } f ? DateOnly.FromDateTime(f) : (DateOnly?)null;
        var to = customTo is { } t ? DateOnly.FromDateTime(t) : (DateOnly?)null;
        customOpen = false;
        Nav.NavigateTo(ReportsHubLinks.Hub(ReportsPeriod.Resolve(ReportsPeriodPreset.Custom, Today, from, to)));
    }

    private bool IsPresetOn(ReportsPeriodPreset preset) =>
        preset == ReportsPeriodPreset.Custom ? customOpen || Period.Preset == preset : !customOpen && Period.Preset == preset;

    // ── Export ─────────────────────────────────────────────────────────

    private bool CanExport => !exporting && (headline is not null || activity is not null);

    private async Task ExportAsync()
    {
        exporting = true;
        exportError = null;
        try
        {
            var bytes = ExportService.ExportReportsHubToExcel(new ReportsHubExport(Period, headline, activity, Attention));
            await JS.InvokeVoidAsync("downloadFile",
                $"Reports_Hub_{Period.From:yyyyMMdd}_{Period.To:yyyyMMdd}.xlsx", Convert.ToBase64String(bytes));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Reports hub export failed");
            exportError = "The workbook could not be built. Try again.";
        }
        finally
        {
            exporting = false;
        }
    }

    // ── What the page shows ────────────────────────────────────────────

    private IReadOnlyList<ReportsAttentionItem> Attention => ReportsAttention.Build(Period, headline, activity, stock);

    private bool AttentionPending => headlineLoading || activityLoading || stockLoading;

    private IReadOnlyList<ReportsDirectoryGroup> Directory => ReportsDirectory.For(user, Period, search);

    private string Freshness => loadedAtCat is { } at ? $"loaded at {at:HH:mm}" : "loading…";

    private sealed record Kpi(
        string Label,
        string Value,
        string? Secondary,
        string? Delta,
        string DeltaTone,
        string? DeltaNote,
        string Foot,
        string FootTone,
        string Href);

    private IReadOnlyList<Kpi> Kpis
    {
        get
        {
            var current = headline?.Current;
            var previous = headline?.Previous;
            var aging = headline?.Aging;
            var note = previous is not null ? $"vs {Period.PreviousLabel}" : null;
            var tiles = new List<Kpi>();

            if (current is not null)
            {
                var (salesDelta, salesTone) = Delta(Change(current.NetRevenueUSD, previous?.NetRevenueUSD));
                tiles.Add(new("Net sales", Usd(current.NetRevenueUSD),
                    current.NetRevenueZIG != 0 ? $"and {Zig(current.NetRevenueZIG)}" : null,
                    salesDelta, salesTone, note,
                    $"{Count(current.TotalInvoices)} {Plural(current.TotalInvoices, "invoice", "invoices")} · after {Usd(current.TotalCreditNotesUSD)} credit notes",
                    "mute", ReportsHubLinks.Tab(ReportsHubLinks.Sales, Period)));

                var (profitDelta, profitTone) = Delta(Change(current.GrossProfitUSD, previous?.GrossProfitUSD));
                var was = previous is { NetSalesBeforeVatUSD: > 0 } ? $" (was {Percent(previous.GrossMarginPercent)})" : string.Empty;
                tiles.Add(new("Gross profit", Usd(current.GrossProfitUSD), null,
                    profitDelta, profitTone, note,
                    $"{Percent(current.GrossMarginPercent)} margin{was}",
                    "mute", ReportsHubLinks.Tab(ReportsHubLinks.Profit, Period)));

                var (cashDelta, cashTone) = Delta(Change(current.TotalCollectedUSD, previous?.TotalCollectedUSD));
                tiles.Add(new("Cash collected", Usd(current.TotalCollectedUSD),
                    current.TotalCollectedZIG != 0 ? $"and {Zig(current.TotalCollectedZIG)}" : null,
                    cashDelta, cashTone, note,
                    $"{Count(current.TotalPayments)} {Plural(current.TotalPayments, "payment", "payments")} · {Percent(current.CollectionRatePercent)} of invoiced",
                    "mute", ReportsHubLinks.Tab(ReportsHubLinks.Payments, Period)));
            }

            if (aging is not null)
            {
                var over90 = aging.Over90Days.AmountUSD;
                tiles.Add(new("Owed to us now", Usd(aging.TotalOutstandingUSD), null,
                    null, "flat", $"{Count(aging.TotalCustomers)} {Plural(aging.TotalCustomers, "customer", "customers")}",
                    over90 > 0 ? $"{Usd(over90)} of it over 90 days" : "Nothing over 90 days",
                    over90 > 0 ? "bad" : "good", ReportsHubLinks.Tab(ReportsHubLinks.Aging, Period)));
            }

            return tiles;
        }
    }

    private static (string? Text, string Tone) Delta(decimal? change) => change switch
    {
        null => (null, "flat"),
        > 0 => ($"▲ {Math.Abs(change.Value):0.0}%", "good"),
        < 0 => ($"▼ {Math.Abs(change.Value):0.0}%", "bad"),
        _ => ("No change", "flat")
    };

    private static string DeltaWords(string tone) => tone switch
    {
        "good" => "up",
        "bad" => "down",
        _ => string.Empty
    };

    // The chart: one bar a day (or a week, for a long period), scaled to a round axis.

    private sealed record Bar(string Tip, double Height, bool Partial, string Tick);

    private sealed record Chart(
        string Title,
        string Range,
        IReadOnlyList<Bar> Bars,
        string AxisTop,
        string AxisMid,
        string Summary,
        string Best,
        string Average,
        string AverageLabel,
        string Invoices);

    private Chart? SalesChart
    {
        get
        {
            var daily = activity?.DailySales;
            if (daily is null)
            {
                return null;
            }

            var byDay = daily.DailySales
                .GroupBy(day => DateOnly.FromDateTime(day.Date))
                .ToDictionary(group => group.Key, group => (Usd: group.Sum(d => d.TotalSalesUSD), Invoices: group.Sum(d => d.InvoiceCount)));
            var from = Period.ChartFrom;
            var days = Enumerable.Range(0, Period.To.DayNumber - from.DayNumber + 1)
                .Select(offset => from.AddDays(offset))
                .Select(date => (Date: date, Totals: byDay.GetValueOrDefault(date)))
                .ToList();

            var weekly = days.Count > MaxDailyBars;
            var points = weekly
                ? days.GroupBy(day => day.Date.AddDays(-(((int)day.Date.DayOfWeek + 6) % 7)))
                    .Select(week => (Date: week.Key, End: week.Max(d => d.Date), Usd: week.Sum(d => d.Totals.Usd), Invoices: week.Sum(d => d.Totals.Invoices)))
                    .ToList()
                : days.Select(day => (day.Date, End: day.Date, day.Totals.Usd, day.Totals.Invoices)).ToList();

            var max = points.Count > 0 ? points.Max(point => point.Usd) : 0m;
            var top = NiceCeiling(max);
            var tickEvery = weekly ? 4 : points.Count <= 16 ? 2 : 7;
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            var bars = points.Select((point, index) =>
            {
                var partial = Period.EndsToday && point.End == Today;
                var label = weekly ? $"Week of {point.Date.ToString("d MMM", culture)}" : point.Date.ToString("ddd d MMM", culture);
                var tip = $"{label} · {Usd(point.Usd)} · {Count(point.Invoices)} {Plural(point.Invoices, "invoice", "invoices")}{(partial ? " so far" : string.Empty)}";
                var showTick = index % tickEvery == 0;
                var tick = !showTick ? string.Empty
                    : index == 0 || point.Date.Day <= (weekly ? 7 : tickEvery) ? point.Date.ToString("d MMM", culture)
                    : point.Date.Day.ToString(culture);
                return new Bar(tip, top > 0 ? (double)(point.Usd / top * 100m) : 0, partial, tick);
            }).ToList();

            // Best and average leave out a day still running, which would always look like the worst.
            var settled = points.Where(point => !(Period.EndsToday && point.End == Today)).ToList();
            var trading = settled.Where(point => point.Usd > 0).ToList();
            var best = settled.Count > 0 ? settled.MaxBy(point => point.Usd) : default;
            var unit = weekly ? "week" : "day";
            var bestText = best.Usd > 0
                ? $"{(weekly ? "Week of " + best.Date.ToString("d MMM", culture) : best.Date.ToString("ddd d MMM", culture))} · {Usd(best.Usd)}"
                : "No sales yet";

            return new Chart(
                weekly ? "Weekly sales" : "Daily sales",
                ReportsPeriod.FormatRange(from, Period.To, withYear: false),
                bars,
                UsdShort(top),
                UsdShort(top / 2),
                $"{(weekly ? "Weekly" : "Daily")} sales from {ReportsPeriod.FormatRange(from, Period.To, true)}, highest {bestText}",
                bestText,
                trading.Count > 0 ? Usd(trading.Average(point => point.Usd)) : "—",
                $"Average trading {unit}",
                Count(points.Sum(point => point.Invoices)));
        }
    }

    /// <summary>The next round axis top at or above <paramref name="value"/>: 1, 2, 2.5 or 5 times a power of ten.</summary>
    public static decimal NiceCeiling(decimal value)
    {
        if (value <= 0)
        {
            return 0;
        }

        var magnitude = (decimal)Math.Pow(10, Math.Floor(Math.Log10((double)value)));
        foreach (var step in new[] { 1m, 2m, 2.5m, 5m, 10m })
        {
            if (step * magnitude >= value)
            {
                return step * magnitude;
            }
        }

        return 10m * magnitude;
    }

    private sealed record AgingRow(string Label, string Amount, string Share, double Width, string Tone);

    private IReadOnlyList<AgingRow> AgingRows
    {
        get
        {
            var aging = headline?.Aging;
            if (aging is null)
            {
                return [];
            }

            var tones = new[] { "good", "info", "warn", "bad" };
            return ReportExportService.ReportsHubAgingBuckets(aging)
                .Select((bucket, index) => new AgingRow(
                    bucket.Label,
                    Usd(bucket.Bucket.AmountUSD),
                    Percent(bucket.Bucket.PercentOfTotal),
                    (double)Math.Clamp(bucket.Bucket.PercentOfTotal, 0m, 100m),
                    tones[index]))
                .ToList();
        }
    }

    private static string SeverityLabel(ReportsAttentionSeverity severity) => severity switch
    {
        ReportsAttentionSeverity.Critical => "Critical",
        ReportsAttentionSeverity.Watch => "Watch",
        _ => "FYI"
    };

    private static string SeverityTone(ReportsAttentionSeverity severity) => severity switch
    {
        ReportsAttentionSeverity.Critical => "bad",
        ReportsAttentionSeverity.Watch => "warn",
        _ => "info"
    };

    private static string Width(double percent) =>
        $"width: {Math.Clamp(percent, 0, 100).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}%";

    private static string Height(double percent) =>
        $"height: {Math.Clamp(percent, 0, 100).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}%";

    private static string Margin(decimal? margin) => margin is { } value ? Percent(value) : "—";

    public void Dispose()
    {
        lifetime.Cancel();
        lifetime.Dispose();
        periodLoad?.Dispose();
    }
}
