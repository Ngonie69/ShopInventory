using System.Globalization;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ShopInventory.Web.Features.Reports.Queries.GetDesktopSalesReview;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;
using Review = ShopInventory.Web.Features.Reports.Queries.GetDesktopSalesReview.DesktopSalesReview;

namespace ShopInventory.Web.Components.Pages;

/// <summary>
/// The business review page: the period's findings first, then the figures behind them.
/// </summary>
/// <remarks>
/// Everything on it — every finding, every figure — comes from the API's review, the same one the
/// scheduled email sends, so the page and the email cannot disagree. The page only lays it out.
/// </remarks>
public partial class DesktopSalesBusinessReview : IDisposable
{
    private const string PeriodLastWeek = "last-week";
    private const string PeriodThisWeek = "this-week";
    private const string PeriodLastMonth = "last-month";
    private const string PeriodThisMonth = "this-month";
    private const string PeriodCustom = "custom";

    /// <summary>The longest period the API will review at once.</summary>
    private const int MaxDays = 186;

    private static readonly (string Key, string Label)[] PeriodOptions =
    [
        (PeriodLastWeek, "Last week"),
        (PeriodThisWeek, "This week"),
        (PeriodLastMonth, "Last month"),
        (PeriodThisMonth, "This month"),
        (PeriodCustom, "Range")
    ];

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private IMasterDataCacheService MasterDataCache { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private ILogger<DesktopSalesBusinessReview> Logger { get; set; } = default!;

    private List<WarehouseDto> warehouses = [];
    private Review? review;
    private string warehouse = "";
    private string period = PeriodLastWeek;
    private DateTime? fromDate;
    private DateTime? toDate;
    private string? currency;
    private bool isLoading;
    private bool isDownloading;
    private bool isDisposed;
    private string? error;
    private CancellationTokenSource loadCts = new();

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

    /// <summary>The currency on screen: the one picked, else the first the review carries.</summary>
    private DesktopSalesReviewCurrency? Section =>
        review?.Currencies.FirstOrDefault(c => c.Currency == currency) ?? review?.Currencies.FirstOrDefault();

    private string Currency => Section?.Currency ?? "";

    protected override async Task OnInitializedAsync()
    {
        var warehouseList = await MasterDataCache.GetWarehousesAsync();
        warehouses = warehouseList?.Where(w => w.IsActive).ToList() ?? [];

        (fromDate, toDate) = Range(period);
        await LoadAsync();
    }

    // ── Loading ──────────────────────────────────────────────────────────

    private async Task LoadAsync()
    {
        if (fromDate is not { } from || toDate is not { } to)
        {
            return;
        }

        if (to < from)
        {
            error = "The period must start on or before the day it ends.";
            return;
        }

        if ((to - from).Days >= MaxDays)
        {
            error = $"At most {MaxDays} days can be reviewed at once.";
            return;
        }

        loadCts.Cancel();
        loadCts.Dispose();
        loadCts = new CancellationTokenSource();
        var cancellationToken = loadCts.Token;

        isLoading = true;
        error = null;

        try
        {
            var result = await Mediator.Send(
                new GetDesktopSalesReviewQuery(from.Date, to.Date, warehouse == "" ? null : warehouse),
                cancellationToken);

            if (cancellationToken.IsCancellationRequested || isDisposed)
            {
                return;
            }

            if (result.IsError)
            {
                error = result.FirstError.Description;
                return;
            }

            review = result.Value;
            if (review.Currencies.All(c => c.Currency != currency))
            {
                currency = review.Currencies.FirstOrDefault()?.Currency;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested && !isDisposed)
            {
                isLoading = false;
            }
        }
    }

    private Task SetPeriodAsync(string key)
    {
        period = key;
        if (key != PeriodCustom)
        {
            (fromDate, toDate) = Range(key);
        }

        return LoadAsync();
    }

    private Task OnFromDateChanged(DateTime? value)
    {
        fromDate = value;
        return LoadAsync();
    }

    private Task OnToDateChanged(DateTime? value)
    {
        toDate = value;
        return LoadAsync();
    }

    private Task ReloadScopeAsync() => LoadAsync();

    private Task RefreshAsync() => LoadAsync();

    private async Task DownloadPdfAsync()
    {
        if (review is null || isDownloading)
        {
            return;
        }

        isDownloading = true;
        try
        {
            var result = await Mediator.Send(new GetDesktopSalesReviewPdfQuery(review.FromDate, review.ToDate, review.WarehouseCode));
            if (result.IsError)
            {
                error = result.FirstError.Description;
                return;
            }

            var scope = review.WarehouseCode is { } code ? $"_{code}" : "";
            await JS.InvokeVoidAsync(
                "downloadFile",
                $"Desktop_Sales_Review{scope}_{review.FromDate:yyyyMMdd}_{review.ToDate:yyyyMMdd}.pdf",
                Convert.ToBase64String(result.Value));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to download the desktop sales review PDF");
            error = $"The PDF could not be downloaded: {ex.Message}";
        }
        finally
        {
            isDownloading = false;
        }
    }

    /// <summary>
    /// The dates a period button stands for, on the counter's calendar. Weeks run Monday to Sunday, as
    /// the weekly email's do.
    /// </summary>
    private static (DateTime From, DateTime To) Range(string key)
    {
        var today = IAuditService.ToCAT(DateTime.UtcNow).Date;
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var firstOfMonth = new DateTime(today.Year, today.Month, 1);

        return key switch
        {
            PeriodThisWeek => (monday, today),
            PeriodLastMonth => (firstOfMonth.AddMonths(-1), firstOfMonth.AddDays(-1)),
            PeriodThisMonth => (firstOfMonth, today),
            _ => (monday.AddDays(-7), monday.AddDays(-1))
        };
    }

    // ── Reading the review ───────────────────────────────────────────────

    private List<DesktopSalesReviewFinding> Findings =>
        review?.Findings.Where(f => f.Currency is null || f.Currency == Currency).ToList() ?? [];

    private string ShopLabel(string warehouseCode) =>
        Section?.ByShop.FirstOrDefault(s => string.Equals(s.WarehouseCode, warehouseCode, StringComparison.OrdinalIgnoreCase))?.Label
        ?? warehouseCode;

    private static List<int> Hours(DesktopSalesReviewCurrency section)
    {
        var traded = section.ByHour.Where(h => h.SalesCount > 0).Select(h => h.Hour).ToList();
        var first = Math.Min(7, traded.DefaultIfEmpty(7).Min());
        var last = Math.Max(18, traded.DefaultIfEmpty(18).Max());
        return Enumerable.Range(first, last - first + 1).ToList();
    }

    private static string SeverityClass(string severity) => severity switch
    {
        "action" => "is-action",
        "review" => "is-review",
        _ => "is-note"
    };

    private static string SeverityLabel(string severity) => severity switch
    {
        "action" => "Act",
        "review" => "Check",
        _ => "Note"
    };

    private static string SeverityIcon(string severity) => severity switch
    {
        "action" => "ph-warning-octagon",
        "review" => "ph-magnifying-glass",
        _ => "ph-info"
    };

    private static string TenderClass(string paymentMethod) => paymentMethod switch
    {
        "Cash" => "ops-dsa-t1",
        "Swipe" => "ops-dsa-t2",
        "Ecocash" => "ops-dsa-t3",
        "Innbucks" => "ops-dsa-t4",
        "Not recorded" => "ops-dsa-tnone",
        _ => "ops-dsa-t5"
    };

    private static string TenderLabel(string paymentMethod) => paymentMethod == "Ecocash" ? "EcoCash" : paymentMethod;

    /// <summary>A shop-by-hour cell's weight, as a share of that shop's own busiest hour.</summary>
    private static string Heat(int sales, int busiest) =>
        busiest == 0 || sales == 0
            ? "0"
            : (0.12m + 0.88m * sales / busiest).ToString("0.###", CultureInfo.InvariantCulture);

    // ── Text ─────────────────────────────────────────────────────────────

    private string Money(decimal value) => $"{Currency} {value.ToString("N2", CultureInfo.InvariantCulture)}".Trim();

    private static string Num(decimal value) => value.ToString("N2", CultureInfo.InvariantCulture);

    private static string Pct(decimal percent) => percent.ToString("0.#", CultureInfo.InvariantCulture) + "%";

    private static string Signed(decimal value) =>
        (value >= 0 ? "+" : "−") + Math.Abs(value).ToString("N2", CultureInfo.InvariantCulture);

    private static string Weight(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Percent(decimal part, decimal whole) =>
        whole <= 0 ? "0%" : (part / whole * 100m).ToString("0.##", CultureInfo.InvariantCulture) + "%";

    private static string Plural(int count, string noun) =>
        $"{count.ToString("N0", CultureInfo.InvariantCulture)} {noun}{(count == 1 ? "" : "s")}";

    private static string DayLabel(DateTime date) => date.ToString("ddd d MMM", CultureInfo.InvariantCulture);

    private static string RangeLabel(DateTime from, DateTime to) =>
        $"{from.ToString("d MMM", CultureInfo.InvariantCulture)} – {to.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}";

    public void Dispose()
    {
        isDisposed = true;
        loadCts.Cancel();
        loadCts.Dispose();
    }
}
