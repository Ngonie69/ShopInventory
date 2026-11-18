using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using ShopInventory.Web.Common;
using ShopInventory.Web.Components.Dashboard;
using ShopInventory.Web.Data;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

/// <summary>
/// The dispatch officer's dashboard at /dispatch-dashboard.
/// </summary>
/// <remarks>
/// <para>Every figure is nullable until its read lands, so the page shows "—" rather than a zero that
/// is about to jump, and each read is caught on its own so one dead service cannot blank the page.</para>
///
/// <para><b>Which warehouses are "dispatch".</b> The factory transfer options name the account's
/// assigned warehouses; an account with none there falls back to its warehouse claims, and an
/// administrator with neither is shown every DISP* warehouse, which is what the factory calls its
/// dispatch stores (DISPCH, DISPYOG, DISPMILK, DISPICE, DISPTUP).</para>
///
/// <para><b>SAP reads run one after another.</b> Each warehouse's transfers are two Service Layer
/// calls (a page and a count), and the six process-wide SAP slots are shared with the tills, so
/// five warehouses fanned out at once would take most of them for a dashboard.</para>
/// </remarks>
public partial class DispatchDashboard
{
    [Inject] private IFactoryTransferService FactoryTransfers { get; set; } = default!;
    [Inject] private IInventoryTransferService TransferService { get; set; } = default!;
    [Inject] private IWarehouseStockCacheService StockCache { get; set; } = default!;
    [Inject] private IMasterDataCacheService MasterData { get; set; } = default!;
    [Inject] private IAuditService AuditService { get; set; } = default!;
    [Inject] private ILogger<DispatchDashboard> Logger { get; set; } = default!;

    [CascadingParameter] private Task<AuthenticationState>? AuthTask { get; set; }

    /// <summary>Rows drawn in each queue panel.</summary>
    private const int RowsShown = 6;

    /// <summary>The factory transfer lists are read one page deep; the API caps a page at 100.</summary>
    private const int FactoryFetchSize = 100;

    /// <summary>A transfer out longer than this is flagged on the page.</summary>
    private const int OverdueHours = 4;

    /// <summary>Where dispatch sends stock, in the order the strip draws them.</summary>
    private static readonly (string Code, string Name)[] KnownExits =
    [
        ("TRANSIT", "Cheeseman"),
        ("DEPOTRAN", "Depots"),
        ("KEFSHOP", "Factory shop")
    ];

    private List<string> warehouses = [];
    private Dictionary<string, string> warehouseNames = new(StringComparer.OrdinalIgnoreCase);
    private string holdingWarehouse = "FSRW";

    private DateTime? loadedAt;
    private bool isLoadingOptions = true;
    private bool isLoadingFactory = true;
    private bool isLoadingSap = true;
    private bool isRefreshing;

    private int? awaitingCount;
    private int? awaitingReleases;
    private int? failedCount;
    private int overdueOutbound;
    private List<FactoryTransferSummaryDto>? awaiting;
    private List<FactoryTransferSummaryDto>? outbound;
    private string? factoryError;

    private int? sentToday;
    private int? sentLinesToday;
    private List<TodayTransfer> todaysTransfers = [];
    private List<Exit> exits = [];
    private string? sapError;

    private List<Holding> holdings = [];

    private string currentUsername = "there";
    private bool _initialized;

    private sealed record TodayTransfer(int DocNum, bool IsInbound, string Route, int Lines);

    private sealed record Exit(string Code, string Name, int Sent, int Lines, int Open);

    private sealed record Holding(string Code, int? Total, int? InStock, int? AtZero);

    protected override async Task OnParametersSetAsync()
    {
        using var pageReads = PageReads.Bind(PageLifetime);
        if (AuthTask is null || _initialized) return;

        var user = (await AuthTask).User;
        if (user.Identity?.IsAuthenticated != true) return;

        _initialized = true;
        currentUsername = user.Identity?.Name ?? currentUsername;

        await ResolveWarehousesAsync(user.FindAll("warehouse").Select(claim => claim.Value));
        await LoadAsync();
        await AuditService.LogAsync(AuditActions.ViewDashboard, "DispatchDashboard", null);
    }

    private async Task RefreshAsync()
    {
        isRefreshing = true;
        try
        {
            await LoadAsync();
        }
        finally
        {
            isRefreshing = false;
        }
    }

    private async Task ResolveWarehousesAsync(IEnumerable<string> claimValues)
    {
        List<WarehouseDto> all = [];
        try
        {
            all = await MasterData.GetWarehousesAsync();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not load warehouses for the dispatch dashboard.");
        }

        warehouseNames = all
            .Where(item => !string.IsNullOrWhiteSpace(item.WarehouseCode))
            .GroupBy(item => item.WarehouseCode!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().WarehouseName ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);

        List<string> assigned = [];
        var (ok, message, options) = await FactoryTransfers.GetOptionsAsync();
        if (ok && options is not null)
        {
            assigned = options.AssignedWarehouses;
            if (!string.IsNullOrWhiteSpace(options.HoldingWarehouseCode))
                holdingWarehouse = options.HoldingWarehouseCode;
        }
        else
        {
            Logger.LogWarning("Dispatch dashboard could not read factory options: {Message}", message);
        }

        var codes = assigned.Count > 0
            ? assigned
            : claimValues.ToList();

        if (codes.Count == 0)
        {
            codes = all
                .Where(item => item.IsActive && item.WarehouseCode?.StartsWith("DISP", StringComparison.OrdinalIgnoreCase) == true)
                .Select(item => item.WarehouseCode!)
                .ToList();
        }

        warehouses = codes
            .Select(code => code.Trim())
            .Where(code => code.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToList();

        holdings = warehouses.Select(code => new Holding(code, null, null, null)).ToList();
        isLoadingOptions = false;
    }

    private async Task LoadAsync()
    {
        if (warehouses.Count == 0)
        {
            isLoadingFactory = isLoadingSap = false;
            return;
        }

        await Task.WhenAll(LoadFactoryAsync(), LoadSapTransfersAsync(), LoadHoldingsAsync());
        BuildExits();
        loadedAt = DateTime.Now;
    }

    private async Task LoadFactoryAsync()
    {
        try
        {
            var waiting = await FactoryTransfers.GetTransfersAsync("awaiting", null, 1, FactoryFetchSize);
            var open = await FactoryTransfers.GetTransfersAsync("open", null, 1, FactoryFetchSize);

            if (!waiting.Success || waiting.Value is null || !open.Success || open.Value is null)
            {
                factoryError = waiting.Success ? open.Message : waiting.Message;
                return;
            }

            factoryError = null;
            awaitingCount = waiting.Value.AwaitingMeCount;
            awaiting = waiting.Value.Items
                .OrderBy(transfer => transfer.SentAtUtc)
                .Take(RowsShown)
                .ToList();
            awaitingReleases = waiting.Value.Items.Count(transfer => transfer.Direction == FactoryTransferStatus.Release);

            outbound = open.Value.Items
                .Where(transfer => !IsMine(transfer.DestinationWarehouseCode)
                                   && transfer.Status is FactoryTransferStatus.InTransit or FactoryTransferStatus.Sending)
                .OrderBy(transfer => transfer.SentAtUtc)
                .ToList();
            overdueOutbound = outbound.Count(transfer => IsOverdue(transfer.SentAtUtc));
            failedCount = open.Value.Items.Count(transfer =>
                transfer.Status is FactoryTransferStatus.SendFailed or FactoryTransferStatus.SettleFailed);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Dispatch dashboard could not read factory transfers.");
            factoryError = "The factory transfers could not be loaded.";
        }
        finally
        {
            isLoadingFactory = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task LoadSapTransfersAsync()
    {
        var today = DateTime.Today;
        var seen = new Dictionary<int, InventoryTransferDto>();
        var failed = 0;

        foreach (var code in warehouses)
        {
            try
            {
                var response = await TransferService.GetTransfersByDateRangeAsync(code, today, today, page: 1, pageSize: 100);
                if (response?.Transfers is null)
                {
                    failed++;
                    continue;
                }

                foreach (var transfer in response.Transfers)
                    seen.TryAdd(transfer.DocEntry, transfer);
            }
            catch (Exception ex)
            {
                failed++;
                Logger.LogWarning(ex, "Dispatch dashboard could not read today's SAP transfers for {Warehouse}.", code);
            }
        }

        sapError = failed == warehouses.Count ? "SAP did not answer for today's transfers." : null;

        var rows = seen.Values
            .OrderByDescending(transfer => transfer.DocNum)
            .Select(transfer =>
            {
                var fromMine = IsMine(transfer.FromWarehouse);
                return new
                {
                    Transfer = transfer,
                    Inbound = !fromMine,
                    LeavesDispatch = fromMine && !IsMine(transfer.ToWarehouse)
                };
            })
            .ToList();

        todaysTransfers = rows
            .Select(row => new TodayTransfer(
                row.Transfer.DocNum,
                row.Inbound,
                $"{row.Transfer.FromWarehouse ?? "—"} → {row.Transfer.ToWarehouse ?? "—"}",
                row.Transfer.Lines?.Count ?? 0))
            .ToList();

        var leaving = rows.Where(row => row.LeavesDispatch).Select(row => row.Transfer).ToList();
        sentToday = sapError is null ? leaving.Count : null;
        sentLinesToday = sapError is null ? leaving.Sum(transfer => transfer.Lines?.Count ?? 0) : null;
        sentByDestination = leaving
            .GroupBy(transfer => ExitCode(transfer.ToWarehouse))
            .ToDictionary(group => group.Key, group => (group.Count(), group.Sum(t => t.Lines?.Count ?? 0)));

        isLoadingSap = false;
        await InvokeAsync(StateHasChanged);
    }

    private Dictionary<string, (int Sent, int Lines)> sentByDestination = new();

    private async Task LoadHoldingsAsync()
    {
        var read = new List<Holding>();
        foreach (var code in warehouses)
        {
            try
            {
                var summary = await StockCache.GetStockSummaryAsync(code);
                if (summary.TotalItems == 0)
                {
                    // The summary reads the cache only, and a dispatch warehouse nobody has opened a
                    // stock page for has never been cached: it would read as holding nothing. The
                    // full read fills an empty cache from the API, then the summary is asked again.
                    await StockCache.GetAllCachedStockAsync(code);
                    summary = await StockCache.GetStockSummaryAsync(code);
                }

                read.Add(new Holding(code, summary.TotalItems, summary.InStockItems, summary.OutOfStockItems));
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Dispatch dashboard could not read the stock summary for {Warehouse}.", code);
                read.Add(new Holding(code, null, null, null));
            }
        }

        holdings = read;
        await InvokeAsync(StateHasChanged);
    }

    private void BuildExits()
    {
        var openByExit = (outbound ?? [])
            .GroupBy(transfer => ExitCode(
                transfer.Direction == FactoryTransferStatus.Delivery ? "DEPOTRAN" : transfer.DestinationWarehouseCode))
            .ToDictionary(group => group.Key, group => group.Count());

        var list = KnownExits
            .Select(known =>
            {
                sentByDestination.TryGetValue(known.Code, out var sent);
                return new Exit(known.Code, known.Name, sent.Sent, sent.Lines, openByExit.GetValueOrDefault(known.Code));
            })
            .ToList();

        if (sentByDestination.TryGetValue("OTHER", out var other) || openByExit.ContainsKey("OTHER"))
            list.Add(new Exit("OTHER", "Elsewhere", other.Sent, other.Lines, openByExit.GetValueOrDefault("OTHER")));

        exits = list;
    }

    // ── Presentation ────────────────────────────────────────────────────────

    private bool IsMine(string? code) =>
        !string.IsNullOrWhiteSpace(code) && warehouses.Contains(code.Trim(), StringComparer.OrdinalIgnoreCase);

    private static string ExitCode(string? destination)
    {
        var code = destination?.Trim().ToUpperInvariant() ?? string.Empty;
        return KnownExits.Any(known => known.Code == code) ? code : "OTHER";
    }

    private int? TotalInStock => holdings.All(h => h.InStock is null) ? null : holdings.Sum(h => h.InStock ?? 0);
    private int? TotalAtZero => holdings.All(h => h.AtZero is null) ? null : holdings.Sum(h => h.AtZero ?? 0);

    private string WarehouseKicker => warehouses.Count == 0 ? "Dispatch" : $"Dispatch · {string.Join(", ", warehouses)}";

    private string HoldingLabel => holdingWarehouse;

    private StatTone WaitingTone => awaitingCount switch
    {
        null => StatTone.Neutral,
        0 => StatTone.Ok,
        _ => StatTone.Warn
    };

    private string? WaitingNote => awaitingCount switch
    {
        null => null,
        0 => "Nothing to count",
        _ when awaiting is { Count: > 0 } => $"Oldest sent {Age(awaiting[0].SentAtUtc)} ago",
        _ => "Waiting on you"
    };

    private static string GreetingText => DateTime.Now.Hour switch
    {
        < 12 => "Good morning",
        < 17 => "Good afternoon",
        _ => "Good evening"
    };

    private string GreetingName => DashboardGreeting.FirstName(currentUsername);

    private static string Figure(int? value) => value?.ToString("N0") ?? "—";

    private string NameFor(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "—";
        return warehouseNames.TryGetValue(code.Trim(), out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : code.Trim();
    }

    private static string Route(FactoryTransferSummaryDto transfer) =>
        $"{transfer.SourceLabel} → {transfer.DestinationWarehouseCode}";

    private static bool IsOverdue(DateTime sentAtUtc) => DateTime.UtcNow - sentAtUtc > TimeSpan.FromHours(OverdueHours);

    private static string StatusTag(FactoryTransferSummaryDto transfer) => transfer.Status switch
    {
        FactoryTransferStatus.SendFailed or FactoryTransferStatus.SettleFailed => "dsh-tag-crit",
        _ when IsOverdue(transfer.SentAtUtc) => "rdb-tag-warn",
        _ => "rdb-tag-info"
    };

    private static string Percent(int? part, int? total) =>
        part is null || total is null or 0
            ? "0%"
            : $"{Math.Round(part.Value * 100d / total.Value, 1).ToString(System.Globalization.CultureInfo.InvariantCulture)}%";

    /// <summary>Coarse age, because the column has room for a few characters.</summary>
    private static string Age(DateTime utc)
    {
        var age = DateTime.UtcNow - utc;
        return age switch
        {
            { TotalMinutes: < 60 } => $"{Math.Max(1, (int)age.TotalMinutes)}m",
            { TotalHours: < 24 } => $"{(int)age.TotalHours}h {age.Minutes:00}m",
            _ => $"{(int)age.TotalDays}d"
        };
    }
}
