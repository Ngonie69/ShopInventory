using System.Globalization;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ShopInventory.Web.Data;
using ShopInventory.Web.Features.Reports.Queries.GetManagementSalesReport;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

public partial class ManagementSalesReport : IDisposable
{
    private const string PeriodWeek = "week";
    private const string PeriodThirtyDays = "30";
    private const string PeriodMonth = "month";
    private const string PeriodLastMonth = "lastmonth";
    private const string PeriodCustom = "custom";

    private static readonly (string Key, string Label)[] PeriodOptions =
    [
        (PeriodWeek, "Last 7 days"),
        (PeriodThirtyDays, "Last 30 days"),
        (PeriodMonth, "This month"),
        (PeriodLastMonth, "Last month"),
        (PeriodCustom, "Range")
    ];

    private const string ByChannel = "channel";
    private const string ByDepot = "depot";
    private const string ByVendor = "vendor";
    private const string ByCostCentre = "costcentre";
    private const string ByOperator = "operator";
    private const string ByPayment = "payment";

    private static readonly (string Key, string Label, string Heading)[] BreakdownOptions =
    [
        (ByDepot, "Shop / depot", "Shop or depot"),
        (ByVendor, "Vendor", "Vendor"),
        (ByChannel, "Channel", "Channel"),
        (ByCostCentre, "Cost centre", "Cost centre"),
        (ByOperator, "Operator", "Operator"),
        (ByPayment, "Payment", "Payment method")
    ];

    private const string ItemsTop = "top";
    private const string ItemsRisers = "risers";
    private const string ItemsFallers = "fallers";
    private const string ItemsLowMargin = "lowmargin";
    private const string ItemsPrice = "price";
    private const string ItemsDiscount = "discount";

    private const string ItemViewItems = "items";
    private const string ItemViewGroups = "groups";
    private const string ItemViewMatrix = "matrix";

    private static readonly (string Key, string Label)[] ItemViewOptions =
    [
        (ItemViewItems, "Items"),
        (ItemViewGroups, "Item groups"),
        (ItemViewMatrix, "Item × shop / depot")
    ];

    private const string MeasureQuantity = "qty";
    private const string MeasureValue = "value";

    private const string DrillDepot = "depot";
    private const string DrillVendor = "vendor";
    private const string DrillChannel = "channel";
    private const string DrillOperator = "operator";

    private static readonly (string Key, string Label)[] DrillOptions =
    [
        (DrillDepot, "Shop / depot"),
        (DrillVendor, "Vendor"),
        (DrillChannel, "Channel"),
        (DrillOperator, "Operator")
    ];

    /// <summary>How many item rows the item × depot grid draws before "show all".</summary>
    private const int MatrixPageSize = 30;

    private static readonly (string Key, string Label)[] ItemOrderOptions =
    [
        (ItemsTop, "Top value"),
        (ItemsRisers, "Rising"),
        (ItemsFallers, "Falling"),
        (ItemsLowMargin, "Lowest margin"),
        (ItemsPrice, "Price rises"),
        (ItemsDiscount, "Most discounted")
    ];

    /// <summary>How many product rows are drawn before "show all" — the rest stay in the export.</summary>
    private const int ItemPageSize = 50;

    private static readonly NocturneSelectOption<string>[] ChannelOptions =
    [
        new(string.Empty, "All channels") { IsUnset = true, RuleAfter = true },
        new("KefalosShopTill", "Shop till"),
        new("KefalosVending", "Vending"),
        new("KefalosVanSales", "Van sales")
    ];

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private IMasterDataCacheService MasterDataCache { get; set; } = default!;
    [Inject] private IReportExportService ExportService { get; set; } = default!;
    [Inject] private IAuditService AuditService { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private ILogger<ManagementSalesReport> Logger { get; set; } = default!;

    private ManagementSalesReportResult? result;
    private string? currency;
    private List<WarehouseDto> warehouses = [];
    private Dictionary<string, string> partnerNames = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<int, string> itemGroupNames = [];

    private string itemView = ItemViewItems;
    private int? itemGroupFilter;
    private bool groupFilterSet;
    private string matrixMeasure = MeasureValue;
    private bool showAllMatrix;

    private string? drillItemCode;
    private ManagementItemAnalysisResult? drill;
    private bool isDrillLoading;
    private string? drillError;
    private string drillBreakdown = DrillDepot;
    private CancellationTokenSource drillCts = new();

    private string warehouse = "";
    private string channel = "";
    private string period = PeriodThirtyDays;
    private DateTime? fromDate = DateTime.Today.AddDays(-29);
    private DateTime? toDate = DateTime.Today;
    private string breakdown = ByDepot;
    private string itemOrder = ItemsTop;
    private string itemQuery = "";
    private bool showAllItems;

    private bool isLoading;
    private bool isExporting;
    private bool hasLoggedView;
    private bool isDisposed;
    private string? error;
    private CancellationTokenSource loadCts = new();

    private ManagementCurrencySection? Current =>
        result?.Currencies.FirstOrDefault(section => section.Currency == currency)
        ?? result?.Currencies.FirstOrDefault();

    private bool HasSales => result is { Currencies.Count: > 0 };

    private IEnumerable<NocturneSelectOption<string>> WarehouseOptions =>
        warehouses
            .Where(w => !string.IsNullOrWhiteSpace(w.WarehouseCode))
            .OrderBy(w => w.WarehouseCode, StringComparer.OrdinalIgnoreCase)
            .Select(w => new NocturneSelectOption<string>(w.WarehouseCode!, w.WarehouseCode!) { Hint = w.WarehouseName })
            .Prepend(NocturneSelectOption.All("All shops & depots"));

    protected override async Task OnInitializedAsync()
    {
        try
        {
            warehouses = (await MasterDataCache.GetWarehousesAsync())?.Where(w => w.IsActive).ToList() ?? [];
            partnerNames = (await MasterDataCache.GetBusinessPartnersAsync())
                .Where(partner => !string.IsNullOrWhiteSpace(partner.CardCode) && !string.IsNullOrWhiteSpace(partner.CardName))
                .GroupBy(partner => partner.CardCode!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().CardName!, StringComparer.OrdinalIgnoreCase);
            itemGroupNames = (await MasterDataCache.GetItemGroupsAsync())
                .Where(group => !string.IsNullOrWhiteSpace(group.GroupName))
                .GroupBy(group => group.Number)
                .ToDictionary(group => group.Key, group => group.First().GroupName!);
        }
        catch (Exception ex)
        {
            // Names and the shop list only; the report itself still loads and shows codes.
            Logger.LogWarning(ex, "Management sales report could not load warehouses or partner names");
        }

        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        loadCts.Cancel();
        loadCts.Dispose();
        loadCts = new CancellationTokenSource();
        var cancellationToken = loadCts.Token;

        isLoading = true;
        error = null;

        try
        {
            var response = await Mediator.Send(
                new GetManagementSalesReportQuery(
                    fromDate,
                    toDate,
                    string.IsNullOrWhiteSpace(warehouse) ? null : warehouse,
                    string.IsNullOrWhiteSpace(channel) ? null : channel),
                cancellationToken);

            if (cancellationToken.IsCancellationRequested || isDisposed)
            {
                return;
            }

            response.SwitchFirst(
                value =>
                {
                    result = value;
                    showAllItems = false;
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
                await AuditService.LogAsync(AuditActions.ViewReports, "Report", nameof(ManagementSalesReport));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load the management sales report page");
            result = null;
            error = "Failed to load the management sales report.";
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
        var monthStart = new DateTime(today.Year, today.Month, 1);

        switch (value)
        {
            case PeriodWeek:
                (fromDate, toDate) = (today.AddDays(-6), today);
                break;
            case PeriodThirtyDays:
                (fromDate, toDate) = (today.AddDays(-29), today);
                break;
            case PeriodMonth:
                (fromDate, toDate) = (monthStart, today);
                break;
            case PeriodLastMonth:
                (fromDate, toDate) = (monthStart.AddMonths(-1), monthStart.AddDays(-1));
                break;
            default:
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
            var bytes = ExportService.ExportManagementSalesReportToExcel(result, itemGroupNames);
            await JS.InvokeVoidAsync(
                "downloadFile",
                $"Management_Sales_Report_{result.FromDate:yyyyMMdd}_{result.ToDate:yyyyMMdd}.xlsx",
                Convert.ToBase64String(bytes));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to export the management sales report");
            error = $"Export failed: {ex.Message}";
        }
        finally
        {
            isExporting = false;
        }
    }

    // ── Breakdowns ───────────────────────────────────────────────────────

    private List<ManagementBreakdownRow> BreakdownRows(ManagementCurrencySection sales) => breakdown switch
    {
        ByVendor => sales.ByVendor,
        ByChannel => sales.ByChannel,
        ByCostCentre => sales.ByCostCentre,
        ByOperator => sales.ByOperator,
        ByPayment => sales.ByPaymentMethod,
        _ => sales.ByDepot
    };

    private string BreakdownHeading => BreakdownOptions.First(option => option.Key == breakdown).Heading;

    private bool BreakdownCarriesMargin => breakdown != ByPayment;

    /// <summary>
    /// The name a row is read by. A vending depot or van comes back as its warehouse with its business
    /// partner as the hint; the partner's name is what a manager knows it by, so it leads where the
    /// cache has it.
    /// </summary>
    private (string Label, string? Hint) RowName(ManagementBreakdownRow row) =>
        breakdown == ByDepot ? DepotName(row.Label, row.Hint) : (row.Label, row.Hint);

    /// <summary>
    /// A shop row arrives named; a vending depot or van arrives as its warehouse with its partner code as
    /// the hint (no "·"), and is named by that partner where the cache knows it.
    /// </summary>
    private (string Label, string? Hint) DepotName(string label, string? hint) =>
        hint is { Length: > 0 } && !hint.Contains('·') && partnerNames.TryGetValue(hint, out var name)
            ? (name, $"{hint} · {label}")
            : (label, hint);

    private List<ManagementItemRow> VisibleItems(ManagementCurrencySection sales)
    {
        var term = itemQuery.Trim();
        var rows = sales.ByItem.Where(item => (!groupFilterSet || item.ItemsGroupCode == itemGroupFilter) && (term.Length == 0
            || item.ItemCode.Contains(term, StringComparison.OrdinalIgnoreCase)
            || (item.ItemDescription?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)));

        rows = itemOrder switch
        {
            ItemsRisers => rows.OrderByDescending(item => item.NetAmount - item.PreviousNetAmount),
            ItemsFallers => rows.OrderBy(item => item.NetAmount - item.PreviousNetAmount),
            // Only items SAP costed can be ranked on margin; the rest follow, by value.
            ItemsLowMargin => rows.OrderBy(item => item.MarginPercent is null ? 1 : 0)
                .ThenBy(item => item.MarginPercent)
                .ThenByDescending(item => item.NetAmount),
            ItemsPrice => rows.OrderBy(item => item.PriceChangePercent is null ? 1 : 0)
                .ThenByDescending(item => item.PriceChangePercent)
                .ThenByDescending(item => item.NetAmount),
            ItemsDiscount => rows.OrderByDescending(item => item.DiscountAmount).ThenByDescending(item => item.NetAmount),
            _ => rows.OrderByDescending(item => item.NetAmount)
        };

        return rows.ToList();
    }

    // ── Health ───────────────────────────────────────────────────────────

    private string BucketValue(ManagementHealthBucket bucket) =>
        bucket.Value.Count == 0
            ? "—"
            : string.Join(" · ", bucket.Value.Select(value => $"{value.Currency} {value.Amount.ToString("N2", CultureInfo.InvariantCulture)}"));

    private static string HealthFamily(ManagementHealthBucket bucket, string whenPresent) =>
        bucket.SalesCount == 0 ? "ops-fam-good" : whenPresent;

    // ── Text ─────────────────────────────────────────────────────────────

    private string PeriodText
    {
        get
        {
            if (result is null)
            {
                return "";
            }

            var where = result.WarehouseCode is { Length: > 0 } code ? code : "all shops & depots";
            return $"{result.FromDate:dd MMM} – {result.ToDate:dd MMM yyyy} · {where}";
        }
    }

    private string ComparedText =>
        result is null ? "" : $"vs {result.PreviousFromDate:dd MMM} – {result.PreviousToDate:dd MMM}";

    private string Money(decimal value) =>
        $"{Current?.Currency} {value.ToString("N2", CultureInfo.InvariantCulture)}".Trim();

    private static string Amount(decimal value) =>
        value == 0 ? "—" : value.ToString("N2", CultureInfo.InvariantCulture);

    private static string OptionalAmount(decimal? value) =>
        value is null ? "—" : value.Value.ToString("N2", CultureInfo.InvariantCulture);

    private static string Pct(decimal? percent) =>
        percent is null ? "—" : percent.Value.ToString("0.#", CultureInfo.InvariantCulture) + "%";

    private static string Percent(decimal part, decimal whole) =>
        whole <= 0 ? "0%" : (part / whole * 100m).ToString("0.##", CultureInfo.InvariantCulture) + "%";

    private static string Plural(int count, string noun) =>
        $"{count.ToString("N0", CultureInfo.InvariantCulture)} {noun}{(count == 1 ? "" : "s")}";

    /// <summary>
    /// A change as words, arrow and family together, so a rise is never told by colour alone. Flat is
    /// neutral rather than good: holding still is not a win.
    /// </summary>
    private static (string Text, string Icon, string Family) Change(decimal? percent) => percent switch
    {
        null => ("new", "ph-sparkle", "ops-fam-neutral"),
        > 0 => ($"+{percent.Value.ToString("0.#", CultureInfo.InvariantCulture)}%", "ph-arrow-up-right", "ops-fam-good"),
        < 0 => ($"{percent.Value.ToString("0.#", CultureInfo.InvariantCulture)}%", "ph-arrow-down-right", "ops-fam-bad"),
        _ => ("0%", "ph-minus", "ops-fam-neutral")
    };

    private static string MarginFamily(decimal? percent) => percent switch
    {
        null => "",
        < 0 => "ops-fam-bad",
        < 10 => "ops-fam-warn",
        _ => ""
    };

    // ── Item groups and the item × depot grid ────────────────────────────

    private string GroupName(int? code) =>
        code is null ? "Not in the product master"
        : itemGroupNames.TryGetValue(code.Value, out var name) ? name
        : $"Group {code}";

    private void ShowGroupItems(int? code)
    {
        itemGroupFilter = code;
        groupFilterSet = true;
        itemView = ItemViewItems;
        showAllItems = false;
    }

    private void ClearGroupFilter()
    {
        itemGroupFilter = null;
        groupFilterSet = false;
    }

    /// <summary>The grid's columns: every shop or depot the items sold at, busiest first, named as the breakdown names them.</summary>
    private List<(string Warehouse, string Label)> MatrixColumns(ManagementCurrencySection sales)
    {
        var sold = sales.ItemDepotMatrix.Select(cell => cell.WarehouseCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return sales.ByDepot
            .Where(row => sold.Contains(row.Key))
            .Select(row => (row.Key, DepotName(row.Label, row.Hint).Label))
            .ToList();
    }

    private List<ManagementItemRow> MatrixRows(ManagementCurrencySection sales)
    {
        var rows = VisibleItems(sales).Where(item => item.NetAmount != 0 || item.Quantity != 0).ToList();
        return showAllMatrix ? rows : rows.Take(MatrixPageSize).ToList();
    }

    private decimal MatrixValue(ManagementCurrencySection sales, string itemCode, string warehouse)
    {
        var cell = sales.ItemDepotMatrix.FirstOrDefault(c =>
            string.Equals(c.ItemCode, itemCode, StringComparison.OrdinalIgnoreCase)
            && string.Equals(c.WarehouseCode, warehouse, StringComparison.OrdinalIgnoreCase));
        return cell is null ? 0m : matrixMeasure == MeasureQuantity ? cell.Quantity : cell.NetAmount;
    }

    /// <summary>
    /// A cell's wash: one hue, stronger with magnitude, against the item's own busiest depot — so the
    /// grid reads "where does this item sell" row by row, and the figure stays in ink on top.
    /// </summary>
    private static string CellWash(decimal value, decimal rowMax) =>
        value <= 0 || rowMax <= 0 ? "" : $"--msr-wash:{(0.08m + 0.34m * value / rowMax).ToString("0.###", CultureInfo.InvariantCulture)}";

    private string Measure(decimal value) =>
        value == 0 ? "—" : matrixMeasure == MeasureQuantity ? value.ToString("#,##0.##", CultureInfo.InvariantCulture) : value.ToString("N2", CultureInfo.InvariantCulture);

    // ── Item drill-down ──────────────────────────────────────────────────

    private async Task OpenItemAsync(string itemCode)
    {
        drillCts.Cancel();
        drillCts.Dispose();
        drillCts = new CancellationTokenSource();
        var cancellationToken = drillCts.Token;

        drillItemCode = itemCode;
        drill = null;
        drillError = null;
        isDrillLoading = true;

        try
        {
            // The report's own filters as it last loaded, so the drill-down measures the same sales.
            var response = await Mediator.Send(
                new GetManagementItemAnalysisQuery(
                    itemCode,
                    result?.FromDate ?? fromDate,
                    result?.ToDate ?? toDate,
                    string.IsNullOrWhiteSpace(warehouse) ? null : warehouse,
                    string.IsNullOrWhiteSpace(channel) ? null : channel),
                cancellationToken);

            if (cancellationToken.IsCancellationRequested || isDisposed)
            {
                return;
            }

            response.SwitchFirst(value => drill = value, failure => drillError = failure.Description);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load the item analysis for {ItemCode}", itemCode);
            drillError = "Failed to load the item analysis.";
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested && !isDisposed)
            {
                isDrillLoading = false;
            }
        }
    }

    private void CloseItem()
    {
        drillCts.Cancel();
        drillItemCode = null;
        drill = null;
        drillError = null;
        isDrillLoading = false;
    }

    /// <summary>The drill-down in the currency the report is showing, else its first.</summary>
    private ManagementItemCurrencySection? DrillSection =>
        drill?.Currencies.FirstOrDefault(section => section.Currency == Current?.Currency)
        ?? drill?.Currencies.FirstOrDefault();

    private List<ManagementItemBreakdownRow> DrillRows(ManagementItemCurrencySection section) => drillBreakdown switch
    {
        DrillVendor => section.ByVendor,
        DrillChannel => section.ByChannel,
        DrillOperator => section.ByOperator,
        _ => section.ByDepot
    };

    private (string Label, string? Hint) DrillRowName(ManagementItemBreakdownRow row) =>
        drillBreakdown == DrillDepot ? DepotName(row.Label, row.Hint) : (row.Label, row.Hint);

    /// <summary>Enter or Space on a focused row does what a click does.</summary>
    private static bool IsActivate(Microsoft.AspNetCore.Components.Web.KeyboardEventArgs e) => e.Key is "Enter" or " ";

    private static string Qty(decimal value) =>
        value == 0 ? "—" : value.ToString("#,##0.##", CultureInfo.InvariantCulture);

    private static string UnitPrice(decimal value) =>
        value == 0 ? "—" : value.ToString("#,##0.00##", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        isDisposed = true;
        drillCts.Cancel();
        drillCts.Dispose();
        loadCts.Cancel();
        loadCts.Dispose();
    }
}
