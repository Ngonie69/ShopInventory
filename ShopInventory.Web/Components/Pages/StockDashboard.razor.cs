using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using ShopInventory.Web.Common;
using ShopInventory.Web.Components.Dashboard;
using ShopInventory.Web.Data;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

/// <summary>
/// The stock controller's dashboard at /stock-dashboard.
///
/// Every figure is nullable until its read lands, so the page shows "—" rather
/// than a zero that is about to jump. Each read is caught separately so one
/// dead service cannot blank the page, and each catch logs.
///
/// Changing warehouse re-runs every warehouse-scoped read behind a version
/// counter, so a slow reply for the warehouse you just left cannot overwrite
/// the one you are now looking at.
/// </summary>
public partial class StockDashboard
{
    [Inject] private IWarehouseStockCacheService StockCache { get; set; } = default!;
    [Inject] private IInventoryTransferService TransferService { get; set; } = default!;
    [Inject] private IMasterDataCacheService MasterData { get; set; } = default!;
    [Inject] private IUserManagementService UserManagement { get; set; } = default!;
    [Inject] private IAuditService AuditService { get; set; } = default!;
    [Inject] private ILogger<StockDashboard> Logger { get; set; } = default!;

    [CascadingParameter] private Task<AuthenticationState>? AuthTask { get; set; }

    /// <summary>How far back the movements panel looks.</summary>
    private const int MovementWindowDays = 7;

    /// <summary>Rows drawn in each panel.</summary>
    private const int RowsShown = 6;

    /// <summary>
    /// The movement window is read one page deep. The API clamps a page to 100,
    /// and only the newest few are drawn, so a second page would cost a round
    /// trip to display nothing.
    /// </summary>
    private const int MovementFetchSize = 100;

    /// <summary>
    /// Transfer requests are read one page deep and never walked: there are
    /// roughly eleven thousand of them and each hundred costs 5-11 seconds, and
    /// walking them is what emptied the Transfers page's Requests tab once
    /// already. This is the same batch size that page and the depot dashboard
    /// read in.
    /// </summary>
    private const int RequestFetchSize = 100;

    private string? warehouse;
    private List<NocturneSelectOption<string>> warehouseOptions = [];
    private Dictionary<string, string> warehouseNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Bumped on every warehouse change so late replies can be dropped.</summary>
    private int dataVersion;

    private DateTime? loadedAt;
    private DateTime? lastSyncedAt;

    private int? totalItems;
    private int? inStockItems;
    private int? outOfStockItems;
    private int? committedItems;
    private int? onOrderItems;

    private int? pendingTransferCount;
    private int? postFailedCount;
    private int? openRequestCount;

    /// <summary>
    /// True when <see cref="openRequestCount"/> is a floor rather than a total,
    /// so the card can render "100+" instead of claiming a figure it does not
    /// have.
    /// </summary>
    private bool openRequestsTruncated;

    private List<PendingInventoryTransferDto>? pendingTransfers;
    private List<Movement>? movements;
    private bool isLoadingPending = true;
    private bool isLoadingMovements = true;

    private string currentUsername = "there";
    private bool _initialized;

    /// <summary>One row of the movements panel, already reduced to what it draws.</summary>
    private sealed record Movement(int DocNum, bool IsInbound, string Counterpart, string When);

    protected override async Task OnParametersSetAsync()
    {
        if (AuthTask is null || _initialized) return;

        var authState = await AuthTask;
        var user = authState.User;

        if (user.Identity?.IsAuthenticated != true) return;

        _initialized = true;
        currentUsername = user.Identity?.Name ?? currentUsername;

        await ResolveWarehouseAsync(user.FindAll("warehouse").Select(claim => claim.Value));
        await LoadAsync();
        await AuditService.LogAsync(AuditActions.ViewDashboard, "StockDashboard", null);
    }

    /// <summary>
    /// The warehouses this account may report on: the ones on its claims, named
    /// from the master-data cache. An account with no claim at all is
    /// unrestricted rather than unassigned — the reading
    /// <see cref="DefaultWarehouseResolver"/> takes — so it is offered every
    /// active warehouse instead of an empty picker.
    /// <para>
    /// Which of them the page opens on is <see cref="HomeDepotResolver"/>'s
    /// decision, and it needs the controller's assigned section — the access
    /// token does not carry it, so it is read from the account's own record.
    /// </para>
    /// </summary>
    private async Task ResolveWarehouseAsync(IEnumerable<string> claimValues)
    {
        var assigned = claimValues
            .Select(value => value.Trim())
            .Where(code => code.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<WarehouseDto> warehouses = [];
        try
        {
            warehouses = await MasterData.GetWarehousesAsync();
        }
        catch (Exception ex)
        {
            // The codes on the claims are enough to run every read on this page;
            // only the names would be missing.
            Logger.LogWarning(ex, "Could not load warehouses for the stock dashboard.");
        }

        warehouseNames = warehouses
            .Where(item => !string.IsNullOrWhiteSpace(item.WarehouseCode))
            .GroupBy(item => item.WarehouseCode!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().WarehouseName ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);

        var codes = assigned.Count > 0
            ? assigned
            : warehouses
                .Where(item => item.IsActive && !string.IsNullOrWhiteSpace(item.WarehouseCode))
                .Select(item => item.WarehouseCode!.Trim())
                .ToList();

        warehouseOptions = codes
            .Select(code => new NocturneSelectOption<string>(code, NameFor(code)) { Hint = code })
            .ToList();

        // The section is the controller's own and cannot change under them, so it
        // is read once here rather than with every panel. A failed read logs
        // inside the service and comes back null, which only leaves the tie to be
        // broken by the order the codes arrived in.
        var me = await UserManagement.GetCurrentUserAsync();

        warehouse = HomeDepotResolver.Resolve(codes, warehouseNames, me?.AssignedSection);
    }

    private async Task OnWarehouseChangedAsync()
    {
        dataVersion++;
        loadedAt = null;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (warehouse is null) return;

        var version = dataVersion;

        await Task.WhenAll(
            LoadStockSummaryAsync(version),
            LoadSyncStatusAsync(version),
            LoadTransferQueuesAsync(version),
            LoadPendingTransfersAsync(version),
            LoadMovementsAsync(version));

        if (version != dataVersion) return;

        // Stamped once every read has landed, so the header's time describes the
        // whole page rather than whichever call returned first.
        loadedAt = DateTime.Now;
    }

    private async Task LoadStockSummaryAsync(int version)
    {
        try
        {
            var summary = await StockCache.GetStockSummaryAsync(warehouse!);
            if (version != dataVersion) return;

            totalItems = summary.TotalItems;
            inStockItems = summary.InStockItems;
            outOfStockItems = summary.OutOfStockItems;
            committedItems = summary.CommittedItems;
            onOrderItems = summary.OnOrderItems;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Stock dashboard could not read the stock summary for {Warehouse}.", warehouse);
        }
        finally
        {
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task LoadSyncStatusAsync(int version)
    {
        try
        {
            var info = await StockCache.GetSyncStatusAsync(warehouse!);
            if (version != dataVersion) return;

            lastSyncedAt = info?.LastSyncedAt;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Stock dashboard could not read the cache sync status for {Warehouse}.", warehouse);
        }
        finally
        {
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task LoadTransferQueuesAsync(int version)
    {
        pendingTransferCount = await CountTransfersAsync(PendingTransferStatuses.AwaitingApproval, version);
        postFailedCount = await CountTransfersAsync(PendingTransferStatuses.PostFailed, version);
        openRequestCount = await CountOpenRequestsAsync(version);

        if (version == dataVersion)
        {
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task<int?> CountTransfersAsync(string status, int version)
    {
        try
        {
            // Only the total is drawn, so ask for the smallest page that still
            // carries an authoritative TotalCount.
            var response = await TransferService.GetPendingTransfersAsync(
                status, warehouseCode: warehouse, pageSize: 1);

            return version == dataVersion ? response?.TotalCount : null;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Stock dashboard could not count {Status} transfers for {Warehouse}.", status, warehouse);
            return null;
        }
    }

    private async Task<int?> CountOpenRequestsAsync(int version)
    {
        try
        {
            // Company-wide on purpose: there is no warehouse-scoped paged
            // endpoint, and the unpaged by-warehouse one silently returns SAP's
            // default 20 rows. The card says so rather than implying a scope it
            // does not have.
            var (response, error) = await TransferService.GetTransferRequestsAsync(
                page: 1, pageSize: RequestFetchSize);

            if (error is not null)
            {
                Logger.LogWarning("Stock dashboard could not count open transfer requests: {Error}", error);
                return null;
            }

            if (version != dataVersion || response is null) return null;

            // The response carries no total — only this page's Count and
            // HasMore — and the pages must not be walked to build one. So a
            // full page is a floor, rendered "100+", the same treatment
            // Products gives a count it could not complete.
            openRequestsTruncated = response.HasMore;
            return response.Count;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Stock dashboard could not count open transfer requests.");
            return null;
        }
    }

    private async Task LoadPendingTransfersAsync(int version)
    {
        try
        {
            var response = await TransferService.GetPendingTransfersAsync(
                PendingTransferStatuses.AwaitingApproval, warehouseCode: warehouse, pageSize: RowsShown);

            if (version != dataVersion) return;

            // Oldest first: the top of the queue should be what has waited longest.
            pendingTransfers = response?.Items
                .OrderBy(transfer => transfer.CreatedAtUtc)
                .Take(RowsShown)
                .ToList();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Stock dashboard could not read the approval queue for {Warehouse}.", warehouse);
        }
        finally
        {
            if (version == dataVersion)
            {
                isLoadingPending = false;
            }

            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task LoadMovementsAsync(int version)
    {
        try
        {
            var today = DateTime.Today;
            var response = await TransferService.GetTransfersByDateRangeAsync(
                warehouse!, today.AddDays(-MovementWindowDays), today, page: 1, pageSize: MovementFetchSize);

            if (version != dataVersion) return;

            movements = response?.Transfers?
                .OrderByDescending(transfer => transfer.DocDate)
                .Take(RowsShown)
                .Select(ToMovement)
                .ToList();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Stock dashboard could not read movements for {Warehouse}.", warehouse);
        }
        finally
        {
            if (version == dataVersion)
            {
                isLoadingMovements = false;
            }

            await InvokeAsync(StateHasChanged);
        }
    }

    /// <summary>
    /// Reduces a transfer to the row the panel draws. Inbound is decided by the
    /// destination matching this warehouse, so a transfer between two warehouses
    /// this account holds still reads correctly from the one being looked at.
    /// </summary>
    private Movement ToMovement(InventoryTransferDto transfer)
    {
        var inbound = string.Equals(transfer.ToWarehouse, warehouse, StringComparison.OrdinalIgnoreCase);
        var counterpart = inbound ? transfer.FromWarehouse : transfer.ToWarehouse;

        return new Movement(
            transfer.DocNum,
            inbound,
            NameFor(counterpart),
            FormatDate(transfer.DocDate));
    }

    // ── Presentation ────────────────────────────────────────────────────────

    private static string GreetingText => DateTime.Now.Hour switch
    {
        < 12 => "Good morning",
        < 17 => "Good afternoon",
        _ => "Good evening"
    };

    /// <summary>A figure that has not landed yet reads as a dash, not a zero.</summary>
    private static string Figure(int? value) => value?.ToString("N0") ?? "—";

    private string NameFor(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "—";

        return warehouseNames.TryGetValue(code.Trim(), out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : code.Trim();
    }

    private string WarehouseLabel =>
        warehouse is null ? "Stock" : $"{NameFor(warehouse)} · {warehouse}";

    private string SyncLabel => lastSyncedAt is { } synced
        ? $"Stock synced {synced.ToLocalTime():dd MMM HH:mm}"
        : "Stock sync time unknown";

    private static string FormatDate(string? value) =>
        DateTime.TryParse(value, out var date) ? date.ToString("dd MMM") : value ?? "—";

    private static string Route(PendingInventoryTransferDto transfer) =>
        $"{transfer.FromWarehouse ?? "—"} → {transfer.ToWarehouse ?? "—"}";

    /// <summary>Coarse age, because the column has room for two characters.</summary>
    private static string Age(DateTime createdAtUtc)
    {
        var age = DateTime.UtcNow - createdAtUtc;

        return age switch
        {
            { TotalMinutes: < 60 } => $"{Math.Max(1, (int)age.TotalMinutes)}m",
            { TotalHours: < 24 } => $"{(int)age.TotalHours}h",
            _ => $"{(int)age.TotalDays}d"
        };
    }

    /// <summary>The open-request figure, marked as a floor when it is one.</summary>
    private string OpenRequestFigure => openRequestCount is null
        ? "—"
        : openRequestsTruncated
            ? $"{openRequestCount.Value:N0}+"
            : openRequestCount.Value.ToString("N0");

    private string OpenRequestNote => openRequestsTruncated
        ? "All warehouses, first page only"
        : "Across all warehouses";

    private StatTone OutOfStockTone => outOfStockItems switch
    {
        null => StatTone.Neutral,
        0 => StatTone.Ok,
        _ => StatTone.Warn
    };

    private string? OutOfStockNote => outOfStockItems switch
    {
        null => null,
        0 => "Everything in stock",
        _ => "Nothing to sell on these"
    };

    private StatTone PendingTone => pendingTransferCount switch
    {
        null => StatTone.Neutral,
        0 => StatTone.Ok,
        _ => StatTone.Warn
    };

    private string? PendingNote => pendingTransferCount switch
    {
        null => null,
        0 => "Queue is clear",
        _ => "Waiting on you"
    };

    private StatTone PostFailedTone => postFailedCount switch
    {
        null => StatTone.Neutral,
        0 => StatTone.Ok,
        _ => StatTone.Critical
    };

    private string? PostFailedNote => postFailedCount switch
    {
        null => null,
        0 => "None stranded",
        _ => "Stranded, need a retry"
    };

    /// <summary>
    /// The first name to greet by, taken from the username. An address local
    /// part carries the name in one of a few shapes — first.last, first_last,
    /// or just the name — so all three reduce to the same greeting.
    /// </summary>
    private string GreetingName
    {
        get
        {
            var localName = currentUsername.Split('@')[0];
            var firstName = localName
                .Replace('.', ' ')
                .Replace('_', ' ')
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(firstName)) return "there";
            return firstName.Length == 1
                ? firstName.ToUpperInvariant()
                : char.ToUpperInvariant(firstName[0]) + firstName[1..];
        }
    }
}
