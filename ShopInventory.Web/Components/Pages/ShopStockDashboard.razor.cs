using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using ShopInventory.Web.Common;
using ShopInventory.Web.Components.Dashboard;
using ShopInventory.Web.Data;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

/// <summary>
/// The factory shop's stock dashboard at /shop-stock-dashboard.
/// </summary>
/// <remarks>
/// <para><b>Days of cover</b> divide what is on the shelf now (SAP's in-stock, through the stock
/// cache) by what the stock ledger says the tills sold over the last week, net of returns. An item
/// the ledger has not seen sell has no rate, so it is never flagged as running low however little
/// there is of it — the at-zero count covers those.</para>
///
/// <para>Quantities are in each item's own unit, so every figure that spans items is a count of
/// items, never a sum of quantities.</para>
/// </remarks>
public partial class ShopStockDashboard
{
    [Inject] private IWarehouseStockCacheService StockCache { get; set; } = default!;
    [Inject] private IStockHistoryService StockHistory { get; set; } = default!;
    [Inject] private IInventoryTransferService TransferService { get; set; } = default!;
    [Inject] private IDesktopIntegrationService DesktopIntegration { get; set; } = default!;
    [Inject] private IMasterDataCacheService MasterData { get; set; } = default!;
    [Inject] private IAuditService AuditService { get; set; } = default!;
    [Inject] private ILogger<ShopStockDashboard> Logger { get; set; } = default!;

    [CascadingParameter] private Task<AuthenticationState>? AuthTask { get; set; }

    /// <summary>The factory shop, which an account with no warehouse of its own opens on.</summary>
    private const string FactoryShop = "KEFSHOP";

    private const int RowsShown = 6;
    private const int MovesShown = 12;
    private const int LowItemsShown = 8;

    /// <summary>The till sales a selling rate is taken over.</summary>
    private const int SalesWindowDays = 7;

    /// <summary>Under this many days' cover an item counts as low on the stock card.</summary>
    private const int LowCoverDays = 2;

    /// <summary>Under this many days' cover an item is listed in Running low.</summary>
    private const int WatchCoverDays = 3;

    private static readonly string[] HeldStatuses =
    [
        PendingTransferStatuses.HeldInSap,
        PendingTransferStatuses.AwaitingApproval,
        PendingTransferStatuses.Approved,
        PendingTransferStatuses.PostFailed
    ];

    private string? warehouse;
    private List<NocturneSelectOption<string>> warehouseOptions = [];
    private Dictionary<string, string> warehouseNames = new(StringComparer.OrdinalIgnoreCase);
    private int dataVersion;

    private DateTime? loadedAt;
    private bool isLoadingStock = true;
    private bool isLoadingTransfers = true;
    private bool isLoadingMoves = true;

    private int? totalItems;
    private int? onShelf;
    private int? atZero;
    private int? lowCount;
    private int? healthy;
    private int oversold;
    private List<LowItem> lowItems = [];
    private string? coverError;

    private List<PendingInventoryTransferDto>? incoming;
    private int? failedPosts;
    private int? receivedToday;
    private int? receivedLinesToday;
    private int? tillSalesToday;

    private List<ShopMove> todayMoves = [];
    private string? movesError;

    private string currentUsername = "there";
    private bool _initialized;

    private sealed record LowItem(string Code, string Name, string? UoM, decimal OnShelf, decimal PerDay, decimal CoverDays);

    private sealed record ShopMove(string Time, string Label, string Tag, string Reference, int Items);

    protected override async Task OnParametersSetAsync()
    {
        using var pageReads = PageReads.Bind(PageLifetime);
        if (AuthTask is null || _initialized) return;

        var user = (await AuthTask).User;
        if (user.Identity?.IsAuthenticated != true) return;

        _initialized = true;
        currentUsername = user.Identity?.Name ?? currentUsername;

        await ResolveWarehouseAsync(user.FindAll("warehouse").Select(claim => claim.Value));
        await LoadAsync();
        await AuditService.LogAsync(AuditActions.ViewDashboard, "ShopStockDashboard", null);
    }

    private async Task ResolveWarehouseAsync(IEnumerable<string> claimValues)
    {
        var assigned = claimValues
            .Select(value => value.Trim())
            .Where(code => code.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<WarehouseDto> all = [];
        try
        {
            all = await MasterData.GetWarehousesAsync();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not load warehouses for the shop stock dashboard.");
        }

        warehouseNames = all
            .Where(item => !string.IsNullOrWhiteSpace(item.WarehouseCode))
            .GroupBy(item => item.WarehouseCode!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().WarehouseName ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);

        // No claim is unrestricted rather than unassigned, the reading the other dashboards take.
        var codes = assigned.Count > 0
            ? assigned
            : all.Where(item => item.IsActive && !string.IsNullOrWhiteSpace(item.WarehouseCode))
                .Select(item => item.WarehouseCode!.Trim())
                .ToList();
        if (codes.Count == 0) codes = [FactoryShop];

        warehouseOptions = codes
            .Select(code => new NocturneSelectOption<string>(code, NameFor(code)) { Hint = code })
            .ToList();

        warehouse = codes.FirstOrDefault(code => code.Equals(FactoryShop, StringComparison.OrdinalIgnoreCase))
                    ?? codes[0];
    }

    private async Task OnWarehouseChangedAsync()
    {
        dataVersion++;
        loadedAt = null;
        isLoadingStock = isLoadingTransfers = isLoadingMoves = true;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (warehouse is null) return;

        var version = dataVersion;
        await Task.WhenAll(
            LoadStockAsync(version),
            LoadTransfersAsync(version),
            LoadTillSalesAsync(version),
            LoadMovesAsync(version));

        if (version == dataVersion) loadedAt = DateTime.Now;
    }

    private async Task LoadStockAsync(int version)
    {
        var code = warehouse!;
        try
        {
            var now = DateTime.UtcNow;
            var stockTask = StockCache.GetAllCachedStockAsync(code);
            var totalsTask = StockHistory.GetMovementTotalsAsync(code, now.AddDays(-SalesWindowDays), now);
            await Task.WhenAll(stockTask, totalsTask);
            if (version != dataVersion) return;

            var products = stockTask.Result?.Products ?? [];
            var (totalsOk, totalsMessage, totals) = totalsTask.Result;

            totalItems = products.Count;
            onShelf = products.Count(p => p.QuantityInStock > 0);
            atZero = products.Count(p => p.QuantityInStock <= 0);
            oversold = products.Count(p => p.QuantityInStock - p.QuantityCommitted < 0);

            if (!totalsOk || totals is null)
            {
                coverError = totalsMessage;
                lowItems = [];
                lowCount = null;
                healthy = onShelf;
                return;
            }

            coverError = null;
            var soldByItem = totals.Items.ToDictionary(t => t.ItemCode, t => t.Sold, StringComparer.OrdinalIgnoreCase);

            var withCover = products
                .Where(p => !string.IsNullOrWhiteSpace(p.ItemCode))
                .Select(p =>
                {
                    var perDay = soldByItem.GetValueOrDefault(p.ItemCode!) / SalesWindowDays;
                    return new LowItem(
                        p.ItemCode!,
                        string.IsNullOrWhiteSpace(p.ItemName) ? p.ItemCode! : p.ItemName!,
                        p.UoM,
                        p.QuantityInStock,
                        perDay,
                        perDay > 0 ? Math.Max(0, p.QuantityInStock) / perDay : decimal.MaxValue);
                })
                .Where(item => item.PerDay > 0)
                .ToList();

            lowCount = withCover.Count(item => item.OnShelf > 0 && item.CoverDays < LowCoverDays);
            healthy = onShelf - lowCount;
            lowItems = withCover
                .Where(item => item.CoverDays < WatchCoverDays)
                .OrderBy(item => item.CoverDays)
                .ThenByDescending(item => item.PerDay)
                .Take(LowItemsShown)
                .ToList();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Shop stock dashboard could not read stock for {Warehouse}.", code);
            coverError = "The shop's stock could not be read.";
        }
        finally
        {
            if (version == dataVersion) isLoadingStock = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task LoadTransfersAsync(int version)
    {
        var code = warehouse!;
        try
        {
            var reads = HeldStatuses
                .Select(status => TransferService.GetPendingTransfersAsync(status, warehouseCode: code, pageSize: 50))
                .ToList();
            var today = DateTime.Today;
            var sapTask = TransferService.GetTransfersByDateRangeAsync(code, today, today, page: 1, pageSize: 100);

            var held = await Task.WhenAll(reads);
            var sap = await sapTask;
            if (version != dataVersion) return;

            var all = held
                .Where(response => response is not null)
                .SelectMany(response => response!.Items)
                .GroupBy(transfer => transfer.Id)
                .Select(group => group.First())
                .ToList();

            incoming = all
                .Where(transfer => IsHere(transfer.ToWarehouse))
                .OrderBy(transfer => transfer.CreatedAtUtc)
                .ToList();
            failedPosts = all.Count(transfer => transfer.Status == PendingTransferStatuses.PostFailed);

            var arrived = sap?.Transfers?.Where(transfer => IsHere(transfer.ToWarehouse)).ToList();
            receivedToday = arrived?.Count;
            receivedLinesToday = arrived?.Sum(transfer => transfer.Lines?.Count ?? 0);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Shop stock dashboard could not read transfers for {Warehouse}.", code);
        }
        finally
        {
            if (version == dataVersion) isLoadingTransfers = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task LoadTillSalesAsync(int version)
    {
        var code = warehouse!;
        try
        {
            var today = DateTime.Today;
            var response = await DesktopIntegration.GetDesktopSalesAsync(
                warehouseCode: code, fromDate: today, toDate: today, page: 1, pageSize: 1);
            if (version == dataVersion) tillSalesToday = response?.TotalCount;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Shop stock dashboard could not count today's till sales for {Warehouse}.", code);
        }
        finally
        {
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task LoadMovesAsync(int version)
    {
        var code = warehouse!;
        try
        {
            var now = DateTime.UtcNow;
            var (ok, message, value) = await StockHistory.GetMovementsAsync(code, StockDayClock.OpeningUtc(now), now);
            if (version != dataVersion) return;

            if (!ok || value is null)
            {
                movesError = message;
                todayMoves = [];
                return;
            }

            movesError = null;
            todayMoves = GroupByDocument(value.Movements);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Shop stock dashboard could not read today's movements for {Warehouse}.", code);
            movesError = "Today's movements could not be read.";
        }
        finally
        {
            if (version == dataVersion) isLoadingMoves = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    /// <summary>
    /// One row per document: the ledger journals a sale once per item, and a page listing every line
    /// would show one sale as six rows. A movement with no document key — an hourly SAP correction —
    /// is grouped with the others of its kind that landed in the same minute.
    /// </summary>
    internal static List<ShopMoveRow> GroupMoves(IEnumerable<StockLedgerMovement> movements) =>
        movements
            .GroupBy(m => (m.Kind, Key: m.DocumentKey ?? $"{m.Reference}|{m.OccurredAtUtc:yyyyMMddHHmm}"))
            .Select(group =>
            {
                var net = group.Sum(m => m.Quantity);
                var last = group.Max(m => m.OccurredAtUtc);
                return new ShopMoveRow(
                    last,
                    group.Key.Kind,
                    StockLedgerKinds.Describe(group.Key.Kind, net),
                    group.First().Reference,
                    group.Select(m => m.ItemCode).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    net);
            })
            .OrderByDescending(row => row.LastAtUtc)
            .ToList();

    internal sealed record ShopMoveRow(DateTime LastAtUtc, string Kind, string Label, string Reference, int Items, decimal Net);

    private static List<ShopMove> GroupByDocument(IEnumerable<StockLedgerMovement> movements) =>
        GroupMoves(movements)
            .Select(row => new ShopMove(
                IAuditService.ToCAT(row.LastAtUtc).ToString("HH:mm"),
                row.Label,
                MoveTag(row.Kind, row.Net),
                row.Reference,
                row.Items))
            .ToList();

    // ── Presentation ────────────────────────────────────────────────────────

    private string StockInHandHref => warehouse is null ? "/stock-in-hand" : $"/stock-in-hand?warehouse={Uri.EscapeDataString(warehouse)}";

    private bool IsHere(string? code) =>
        warehouse is not null && string.Equals(code?.Trim(), warehouse, StringComparison.OrdinalIgnoreCase);

    private string? IncomingNote => incoming switch
    {
        null => null,
        { Count: 0 } => "Nothing on its way",
        _ => $"Oldest raised {Age(incoming[0].CreatedAtUtc)} ago"
    };

    private static string Standing(PendingInventoryTransferDto transfer) => transfer.Status switch
    {
        PendingTransferStatuses.HeldInSap => transfer.SapApprovalState switch
        {
            FactoryTransferSapStates.Approved => "Approved in SAP",
            FactoryTransferSapStates.Pending => "Waiting on SAP approval",
            FactoryTransferSapStates.Rejected => "Rejected in SAP",
            _ => "To add in SAP"
        },
        PendingTransferStatuses.AwaitingApproval => "Awaiting approval",
        PendingTransferStatuses.Approved => "Approved, posting",
        PendingTransferStatuses.PostFailed => "Post failed",
        _ => transfer.Status
    };

    private static string StandingTag(PendingInventoryTransferDto transfer) => transfer.Status switch
    {
        PendingTransferStatuses.PostFailed => "dsh-tag-crit",
        PendingTransferStatuses.HeldInSap when transfer.SapApprovalState == FactoryTransferSapStates.Rejected => "dsh-tag-crit",
        PendingTransferStatuses.Approved => "dsh-tag-ok",
        PendingTransferStatuses.HeldInSap when transfer.SapApprovalState == FactoryTransferSapStates.Approved => "dsh-tag-ok",
        _ => "rdb-tag-warn"
    };

    private static string MoveTag(string kind, decimal net) => kind switch
    {
        StockLedgerKinds.Reconciliation => string.Empty,
        StockLedgerKinds.Transfer => net >= 0 ? "dsh-tag-ok" : "rdb-tag-warn",
        _ => net < 0 ? "rdb-tag-info" : "dsh-tag-ok"
    };

    private static string CoverLabel(decimal days) => days <= 0 ? "Out" : $"{days:0.0} d";

    private static string CoverClass(decimal days) => days < 1 ? "rdb-bad" : days < LowCoverDays ? "rdb-warn" : "rdb-good";

    private static string CoverMeterClass(decimal days) => days < 1 ? "rdb-meter-zero" : days < LowCoverDays ? "rdb-meter-low" : "rdb-meter-stock";

    private static string CoverWidth(decimal days) =>
        $"{Math.Clamp(Math.Round(days / 4m * 100m), 4m, 100m).ToString(CultureInfo.InvariantCulture)}%";

    internal static string Quantity(decimal value, string? uom)
    {
        var number = value == Math.Round(value) ? value.ToString("N0", CultureInfo.InvariantCulture) : value.ToString("N2", CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(uom) ? number : $"{number} {uom}";
    }

    private static string Percent(int? part, int? total) =>
        part is null || total is null or 0
            ? "0%"
            : $"{Math.Round(part.Value * 100d / total.Value, 1).ToString(CultureInfo.InvariantCulture)}%";

    private static string Figure(int? value) => value?.ToString("N0") ?? "—";

    private string NameFor(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "Factory shop";
        return warehouseNames.TryGetValue(code.Trim(), out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : code.Trim();
    }

    private static string GreetingText => DateTime.Now.Hour switch
    {
        < 12 => "Good morning",
        < 17 => "Good afternoon",
        _ => "Good evening"
    };

    private string GreetingName => DashboardGreeting.FirstName(currentUsername);

    private static string Age(DateTime utc)
    {
        var age = DateTime.UtcNow - utc;
        return age switch
        {
            { TotalMinutes: < 60 } => $"{Math.Max(1, (int)age.TotalMinutes)}m",
            { TotalHours: < 24 } => $"{(int)age.TotalHours}h",
            _ => $"{(int)age.TotalDays}d"
        };
    }
}
