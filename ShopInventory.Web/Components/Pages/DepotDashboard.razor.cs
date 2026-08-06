using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using ShopInventory.Web.Common;
using ShopInventory.Web.Components;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

/// <summary>
/// The DepotController landing page, rendered by <see cref="Home"/> at
/// /dashboard in place of the cashier dashboard — the arrangement
/// <see cref="SalesRepDashboard"/> already has beside it.
/// </summary>
/// <remarks>
/// A depot controller works two modules, Transfers and Local Stock, against the
/// warehouses assigned to their account. Everything here is scoped to one of
/// those depots, so the four reads are: the movement window (SAP), the open
/// transfer requests (SAP), the drafts this app is holding for approval (the
/// app's own database) and today's stock snapshot.
/// <para>
/// Each read owns its own panel, its own failure flag and its own version
/// counter, so one slow or unavailable source never blanks the others — and the
/// range control, which only the movement window depends on, cannot discard a
/// snapshot that was still in flight.
/// </para>
/// </remarks>
public partial class DepotDashboard
{
    [Inject] private IInventoryTransferService TransferService { get; set; } = default!;
    [Inject] private IDesktopIntegrationService DesktopService { get; set; } = default!;
    [Inject] private IMasterDataCacheService MasterData { get; set; } = default!;
    [Inject] private IUserManagementService UserManagement { get; set; } = default!;
    [Inject] private ILogger<DepotDashboard> Logger { get; set; } = default!;

    /// <summary>
    /// The controller's first name, already derived by Home — this page never
    /// shows the raw username, which is an email address for most accounts.
    /// </summary>
    [Parameter] public string DisplayName { get; set; } = string.Empty;

    [CascadingParameter] private Task<AuthenticationState>? AuthTask { get; set; }

    /// <summary>The ranges the segmented control offers.</summary>
    private static readonly int[] RangeOptions = [7, 14, 30];

    /// <summary>
    /// One page of movements per window. The API clamps a page to 100, and the
    /// count comes from a separate query, so the page can say when a window ran
    /// past what was fetched — see <see cref="movementsTruncated"/>.
    /// </summary>
    private const int MovementFetchSize = 100;

    /// <summary>
    /// Open transfer requests, newest first, across every warehouse — the API
    /// has no warehouse-scoped page and the one unpaged by-warehouse endpoint
    /// silently returns SAP's default 20 rows. This is also the batch size the
    /// Transfers page reads its own Requests tab in.
    /// </summary>
    private const int RequestFetchSize = 100;

    /// <summary>Held drafts for this depot. The API clamps a page to 200.</summary>
    private const int HeldFetchSize = 200;

    /// <summary>
    /// At or below this, an item is reported as running low rather than
    /// comfortable. It is the step /local-stock colours its quantities on, so
    /// the two pages agree about what "low" means.
    /// </summary>
    private const decimal LowStockThreshold = 10m;

    private string? depot;
    private List<NocturneSelectOption<string>> depotOptions = [];
    private Dictionary<string, string> warehouseNames = new(StringComparer.OrdinalIgnoreCase);

    private int rangeDays = 14;
    private DateTime? readAt;
    private string? loadError;

    // ── The movement window ─────────────────────────────────────────────────
    private int movementVersion;
    private bool isLoadingMovements = true;
    private bool movementsFailed;
    private bool movementsTruncated;
    private int movementTotal;
    private int inToday;
    private int outToday;
    private int rangeIn;
    private int rangeOut;
    private int axisMax;
    private IReadOnlyList<ChartBar> bars = [];
    private IReadOnlyList<MovementRow> recentMovements = [];

    // ── The three panels that describe a state rather than a window ─────────
    private int dataVersion;

    private bool isLoadingRequests = true;
    private bool requestsFailed;
    private bool requestsTruncated;
    private IReadOnlyList<RequestRow> toFulfil = [];
    private IReadOnlyList<RequestRow> incoming = [];

    private bool isLoadingHeld = true;
    private bool heldFailed;
    private int? awaitingApprovalCount;
    private int decidableCount;
    private DateTime? oldestHeld;
    private int? notPostedCount;
    private int postFailedCount;

    private bool isLoadingStock = true;
    private bool stockFailed;
    private StockHealth? stock;

    private static DateTime Today => DateTime.Today;

    private DateTime RangeStart => Today.AddDays(-(rangeDays - 1));

    private bool IsLoading => isLoadingMovements || isLoadingRequests || isLoadingHeld || isLoadingStock;

    private bool RequestsLoaded => !isLoadingRequests && !requestsFailed;

    private int? MovementsToday => isLoadingMovements || movementsFailed ? null : inToday + outToday;

    private int? OpenRequestCount => RequestsLoaded ? toFulfil.Count : (int?)null;

    private int? AwaitingApprovalCount => awaitingApprovalCount;

    private int? NotPostedCount => notPostedCount;

    private static string Greeting => DateTime.Now.Hour switch
    {
        < 12 => "Good morning",
        < 17 => "Good afternoon",
        _ => "Good evening"
    };

    private string Greeted => string.IsNullOrWhiteSpace(DisplayName) ? "there" : DisplayName;

    private string DepotLabel =>
        depot is null
            ? "No depot assigned"
            : warehouseNames.TryGetValue(depot, out var name) && !string.IsNullOrWhiteSpace(name)
                ? $"{depot} · {name}"
                : depot;

    /// <summary>
    /// What is actually waiting, in one sentence. It names only the queues that
    /// have something in them, so a clear depot reads as clear rather than as
    /// three zeroes.
    /// </summary>
    private string Standfirst
    {
        get
        {
            if (depot is null)
            {
                return "This account has no warehouse assigned, so there is no depot to report on.";
            }

            if (isLoadingRequests && isLoadingHeld)
            {
                return "Pulling the movement window, the request queue and today's stock together…";
            }

            List<string> clauses = [];

            if (OpenRequestCount is > 0 and var requests)
            {
                clauses.Add($"{requests:N0} {(requests == 1 ? "request needs" : "requests need")} stock out of here");
            }

            if (AwaitingApprovalCount is > 0 and var held)
            {
                clauses.Add($"{held:N0} {(held == 1 ? "draft is" : "drafts are")} waiting on approval");
            }

            if (NotPostedCount is > 0 and var stuck)
            {
                clauses.Add($"{stuck:N0} approved {(stuck == 1 ? "draft has" : "drafts have")} not reached SAP");
            }

            return clauses.Count == 0
                ? "Nothing is waiting on you: no open request on this depot, and every approved transfer has posted."
                : string.Join(", ", clauses) + ".";
        }
    }

    private string FulfilNote
    {
        get
        {
            if (!RequestsLoaded) return isLoadingRequests ? "Reading…" : "Unavailable";
            if (toFulfil.Count == 0) return "Nothing waiting on this depot";

            var oldest = toFulfil.Where(row => row.Raised is not null).Min(row => row.Raised);
            return oldest is { } raised
                ? $"Oldest raised {Age(raised)}"
                : $"{toFulfil.Count:N0} waiting";
        }
    }

    private string ApprovalNote
    {
        get
        {
            if (isLoadingHeld) return "Reading…";
            if (heldFailed) return "Unavailable";
            if (awaitingApprovalCount is not > 0) return "Nothing waiting on a decision";

            return decidableCount > 0
                ? $"{decidableCount:N0} you can decide"
                : oldestHeld is { } raised
                    ? $"Oldest raised {Age(raised)}"
                    : "With another approver";
        }
    }

    private string NotPostedNote
    {
        get
        {
            if (isLoadingHeld) return "Reading…";
            if (heldFailed) return "Unavailable";
            if (notPostedCount is not > 0) return "Every approved draft posted";

            return postFailedCount > 0
                ? $"{postFailedCount:N0} failed on the last attempt"
                : "Approved, never attempted";
        }
    }

    private string StockNote
    {
        get
        {
            if (isLoadingStock) return "Reading…";
            if (stock is not { } snapshot) return "No snapshot today";

            return $"of {snapshot.Total:N0} items · {snapshot.Low:N0} running low";
        }
    }

    private static readonly WorkflowStep[] Steps =
    [
        new("1",
            "Ask for stock",
            "Raise a transfer request against the warehouse holding what this depot needs.",
            "/transfer-request/create",
            "New request"),
        new("2",
            "Move it and approve it",
            "Post a transfer out of the depot, and clear the drafts the approval process is holding.",
            "/inventory-transfers",
            "Open Transfers"),
        new("3",
            "Check what landed",
            "Read today's snapshot, already adjusted for the transfers and sales that have gone through.",
            "/local-stock",
            "Open Local Stock")
    ];

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // The shell paints first and the panels fill in: the movement window
        // and the request queue both reach SAP, and a controller should not
        // watch a blank page while they do.
        if (!firstRender) return;

        await ResolveDepotAsync();
        await LoadAllAsync();
    }

    /// <summary>
    /// The depots this account may report on: the warehouses assigned to it,
    /// named from the master-data cache. An account with no assignment at all is
    /// unrestricted rather than unassigned — the same reading
    /// <see cref="DefaultWarehouseResolver"/> takes — so it is offered every
    /// active warehouse instead of an empty picker.
    /// <para>
    /// Which of them the page opens on is <see cref="HomeDepotResolver"/>'s
    /// decision, and it needs the controller's assigned section — the access
    /// token does not carry it, so it is read from the account's own record.
    /// </para>
    /// </summary>
    private async Task ResolveDepotAsync()
    {
        List<string> assigned = [];

        if (AuthTask is not null)
        {
            var user = (await AuthTask).User;
            assigned = user.FindAll("warehouse")
                .Select(claim => claim.Value.Trim())
                .Where(code => code.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        List<WarehouseDto> warehouses = [];
        try
        {
            warehouses = await MasterData.GetWarehousesAsync();
        }
        catch (Exception ex)
        {
            // The codes on the claims are enough to run every read on this page;
            // only the names would be missing.
            Logger.LogWarning(ex, "Could not load warehouses for the depot dashboard");
        }

        warehouseNames = warehouses
            .Where(warehouse => !string.IsNullOrWhiteSpace(warehouse.WarehouseCode))
            .GroupBy(warehouse => warehouse.WarehouseCode!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().WarehouseName ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);

        var codes = assigned.Count > 0
            ? assigned
            : warehouses
                .Where(warehouse => warehouse.IsActive && !string.IsNullOrWhiteSpace(warehouse.WarehouseCode))
                .Select(warehouse => warehouse.WarehouseCode!.Trim())
                .ToList();

        depotOptions = codes
            .Select(code => new NocturneSelectOption<string>(
                code,
                warehouseNames.TryGetValue(code, out var name) && !string.IsNullOrWhiteSpace(name) ? name : code)
            {
                Hint = code
            })
            .ToList();

        // The section is the controller's own and cannot change under them, so it
        // is read once here rather than with every panel. A failed read logs
        // inside the service and comes back null, which only leaves the tie to be
        // broken by the order the codes arrived in.
        var me = await UserManagement.GetCurrentUserAsync();

        depot = HomeDepotResolver.Resolve(codes, warehouseNames, me?.AssignedSection);
        await InvokeAsync(StateHasChanged);
    }

    private async Task SelectRangeAsync(int days)
    {
        if (days == rangeDays || isLoadingMovements) return;

        rangeDays = days;
        await LoadMovementsAsync(++movementVersion);
    }

    private async Task OnDepotChangedAsync() => await LoadAllAsync();

    private async Task RefreshAsync()
    {
        if (IsLoading) return;
        await LoadAllAsync();
    }

    /// <summary>
    /// Both versions are stamped here rather than inside the loaders: the three
    /// panels share one counter, so a loader that bumped it on the way in would
    /// invalidate the two started beside it and leave them reading "Loading…"
    /// for good.
    /// </summary>
    private async Task LoadAllAsync()
    {
        var movement = ++movementVersion;
        var data = ++dataVersion;
        readAt = null;

        await Task.WhenAll(
            LoadMovementsAsync(movement),
            LoadRequestsAsync(data),
            LoadHeldDraftsAsync(data),
            LoadStockAsync(data));

        // Stamped once every read has landed, so the header's time describes the
        // whole page rather than whichever call returned first.
        readAt = DateTime.Now;
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// The window's transfers, which drive the chart, the two day figures and
    /// the movement queue. SAP is asked for everything that touched the depot in
    /// either direction, and the direction is read off each document's header.
    /// </summary>
    private async Task LoadMovementsAsync(int version)
    {
        if (depot is not { } warehouse)
        {
            isLoadingMovements = false;
            return;
        }

        isLoadingMovements = true;
        movementsFailed = false;
        await InvokeAsync(StateHasChanged);

        try
        {
            var response = await TransferService.GetTransfersByDateRangeAsync(
                warehouse, RangeStart, Today, page: 1, pageSize: MovementFetchSize);

            if (version != movementVersion) return;

            if (response is null)
            {
                movementsFailed = true;
                return;
            }

            ApplyMovements(response, warehouse);
        }
        catch (Exception ex)
        {
            if (version != movementVersion) return;

            Logger.LogWarning(ex, "Failed to read the movement window for depot {Depot}", warehouse);
            movementsFailed = true;
        }
        finally
        {
            if (version == movementVersion)
            {
                isLoadingMovements = false;
                UpdateLoadError();
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    private void ApplyMovements(InventoryTransferDateResponse response, string warehouse)
    {
        var transfers = response.Transfers ?? [];

        movementTotal = Math.Max(response.TotalCount, transfers.Count);
        movementsTruncated = transfers.Count < movementTotal;

        var dailyIn = new int[rangeDays];
        var dailyOut = new int[rangeDays];
        var rows = new List<MovementRow>();

        inToday = outToday = rangeIn = rangeOut = 0;

        // Newest first: the API orders by DocEntry descending, and a document's
        // entry number and its date only disagree on backdated paperwork.
        foreach (var transfer in transfers)
        {
            var direction = DirectionOf(transfer, warehouse);
            var raised = ParseDocDate(transfer.DocDate);
            var counterparty = direction switch
            {
                Flow.In => transfer.FromWarehouse,
                Flow.Out => transfer.ToWarehouse,
                _ => transfer.ToWarehouse ?? transfer.FromWarehouse
            };

            if (rows.Count < 7)
            {
                rows.Add(new MovementRow(
                    transfer.DocNum > 0 ? $"#{transfer.DocNum}" : $"E{transfer.DocEntry}",
                    DirectionLabel(direction),
                    DirectionTone(direction),
                    DirectionIcon(direction),
                    WarehouseLabel(counterparty),
                    (transfer.Lines?.Count ?? 0).ToString("N0"),
                    raised is { } when ? When(when) : "—"));
            }

            if (raised is not { } date) continue;

            var day = (date.Date - RangeStart).Days;
            var inWindow = day >= 0 && day < rangeDays;

            // A document that both left and arrived here is a move inside the
            // depot: it crosses no boundary, so it is listed but counted in
            // neither direction — which is what keeps the two figures adding up
            // to the movement total.
            if (direction == Flow.In)
            {
                if (inWindow) dailyIn[day]++;
                rangeIn++;
                if (date.Date == Today) inToday++;
            }
            else if (direction == Flow.Out)
            {
                if (inWindow) dailyOut[day]++;
                rangeOut++;
                if (date.Date == Today) outToday++;
            }
        }

        recentMovements = rows;
        BuildBars(dailyIn, dailyOut);
    }

    private void BuildBars(int[] dailyIn, int[] dailyOut)
    {
        var peak = Math.Max(
            dailyIn.Length == 0 ? 0 : dailyIn.Max(),
            dailyOut.Length == 0 ? 0 : dailyOut.Max());

        axisMax = NiceCeiling(peak);

        var built = new List<ChartBar>(dailyIn.Length);
        var last = dailyIn.Length - 1;

        for (var i = 0; i < dailyIn.Length; i++)
        {
            var date = RangeStart.AddDays(i);
            var into = dailyIn[i];
            var outOf = dailyOut[i];

            // A day with one document against an axis of thirty would round away
            // to nothing, so anything that moved keeps a readable stub.
            var inHeight = Height(into, axisMax);
            var outHeight = Height(outOf, axisMax);

            // The read-out sits over the middle of the column, so the two at
            // either end pin it to their own edge rather than hanging past the
            // card.
            var tip = i <= 1 ? "is-start" : i >= last - 1 ? "is-end" : string.Empty;

            var total = into + outOf;
            var note = total switch
            {
                0 => "Nothing moved",
                1 => "1 document",
                _ => $"{total} documents"
            };

            built.Add(new ChartBar(
                inHeight,
                outHeight,
                tip,
                i == last,
                date.ToString("ddd dd MMM", CultureInfo.CurrentCulture),
                into,
                outOf,
                note,
                $"{date:ddd dd MMM}: {into} in, {outOf} out"));
        }

        bars = built;
    }

    /// <summary>
    /// The open transfer requests either side of this depot: the ones asking it
    /// for stock, and the ones it has out for stock of its own.
    /// </summary>
    /// <remarks>
    /// SAP is asked for the newest page of open requests across every warehouse
    /// and the depot's are picked out here. There is no warehouse-scoped page to
    /// ask for: the by-warehouse endpoint is unpaged, filters on the receiving
    /// warehouse alone, and — carrying no <c>Prefer: odata.maxpagesize</c> —
    /// returns SAP's default 20 rows whatever the warehouse holds.
    /// </remarks>
    private async Task LoadRequestsAsync(int version)
    {
        if (depot is not { } warehouse)
        {
            isLoadingRequests = false;
            return;
        }

        isLoadingRequests = true;
        requestsFailed = false;
        await InvokeAsync(StateHasChanged);

        try
        {
            var (response, error) = await TransferService.GetTransferRequestsAsync(
                page: 1, pageSize: RequestFetchSize, status: "open");

            if (version != dataVersion) return;

            if (response is null)
            {
                Logger.LogWarning("Open transfer requests unavailable for depot {Depot}: {Error}", warehouse, error);
                requestsFailed = true;
                return;
            }

            var requests = response.TransferRequests ?? [];
            requestsTruncated = response.HasMore;

            toFulfil = requests.Where(request => Same(request.FromWarehouse, warehouse))
                .Select(request => ToRow(request, request.ToWarehouse))
                .ToList();

            incoming = requests.Where(request => Same(request.ToWarehouse, warehouse))
                .Select(request => ToRow(request, request.FromWarehouse))
                .ToList();
        }
        catch (Exception ex)
        {
            if (version != dataVersion) return;

            Logger.LogWarning(ex, "Failed to read open transfer requests for depot {Depot}", warehouse);
            requestsFailed = true;
        }
        finally
        {
            if (version == dataVersion)
            {
                isLoadingRequests = false;
                UpdateLoadError();
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    private RequestRow ToRow(InventoryTransferRequestDto request, string? counterparty)
    {
        var raised = ParseDocDate(request.DocDate);

        return new RequestRow(
            request.DocNum > 0 ? $"#{request.DocNum}" : $"E{request.DocEntry}",
            WarehouseLabel(counterparty),
            raised,
            raised is { } date ? Age(date) : "—");
    }

    /// <summary>
    /// The transfers this app is holding: drafts still awaiting a decision, and
    /// approved drafts that are not in SAP — whether the post failed or was
    /// never attempted. The second group is the one nobody is watching for, so
    /// it gets a queue of its own rather than a line in a status filter.
    /// </summary>
    private async Task LoadHeldDraftsAsync(int version)
    {
        if (depot is not { } warehouse)
        {
            isLoadingHeld = false;
            return;
        }

        isLoadingHeld = true;
        heldFailed = false;
        await InvokeAsync(StateHasChanged);

        try
        {
            var response = await TransferService.GetPendingTransfersAsync(
                status: "all", warehouseCode: warehouse, pageSize: HeldFetchSize);

            if (version != dataVersion) return;

            if (response is null)
            {
                heldFailed = true;
                return;
            }

            var waiting = response.Items
                .Where(item => item.Status == PendingTransferStatuses.AwaitingApproval)
                .ToList();

            awaitingApprovalCount = waiting.Count;
            decidableCount = waiting.Count(item => item.CanCurrentUserDecide);
            oldestHeld = waiting.Count > 0 ? waiting.Min(item => item.CreatedAtUtc).ToLocalTime() : null;

            postFailedCount = response.Items.Count(item => item.Status == PendingTransferStatuses.PostFailed);

            // An approved draft carrying no SAP document number never made it,
            // whether the post failed outright or was cut short before it
            // started. Both are stock that has not moved.
            notPostedCount = postFailedCount + response.Items.Count(
                item => item.Status == PendingTransferStatuses.Approved && item.SapDocNum is null);
        }
        catch (Exception ex)
        {
            if (version != dataVersion) return;

            Logger.LogWarning(ex, "Failed to read held transfer drafts for depot {Depot}", warehouse);
            heldFailed = true;
        }
        finally
        {
            if (version == dataVersion)
            {
                isLoadingHeld = false;
                UpdateLoadError();
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    /// <summary>
    /// Today's stock snapshot for the depot, counted by item rather than summed:
    /// a warehouse holds items in kilograms, litres and each, so a total
    /// quantity across them would not mean anything.
    /// </summary>
    private async Task LoadStockAsync(int version)
    {
        if (depot is not { } warehouse)
        {
            isLoadingStock = false;
            return;
        }

        isLoadingStock = true;
        stockFailed = false;
        stock = null;
        await InvokeAsync(StateHasChanged);

        try
        {
            var result = await DesktopService.GetLocalStockAsync(warehouse, Today);

            if (version != dataVersion) return;

            // The service reports a missing snapshot and a failed call the same
            // way, so the panel says the thing that is true either way: there is
            // nothing to read for today.
            if (result is null) return;

            stock = new StockHealth(
                result.Items.Count,
                result.Items.Count(item => item.AvailableQuantity > LowStockThreshold),
                result.Items.Count(item => item.AvailableQuantity > 0 && item.AvailableQuantity <= LowStockThreshold),
                result.Items.Count(item => item.AvailableQuantity <= 0),
                result.Items.Count(item => item.TransferAdjustment != 0),
                result.SnapshotStatus,
                result.SnapshotDate);
        }
        catch (Exception ex)
        {
            if (version != dataVersion) return;

            Logger.LogWarning(ex, "Failed to read the stock snapshot for depot {Depot}", warehouse);
            stockFailed = true;
        }
        finally
        {
            if (version == dataVersion)
            {
                isLoadingStock = false;
                UpdateLoadError();
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    /// <summary>
    /// Names the panels that could not be read. A panel that failed says so in
    /// its own place as well; this is the one line that says the page as a whole
    /// is incomplete.
    /// </summary>
    private void UpdateLoadError()
    {
        List<string> failed = [];
        if (movementsFailed) failed.Add("the movement window");
        if (requestsFailed) failed.Add("transfer requests");
        if (heldFailed) failed.Add("held drafts");
        if (stockFailed) failed.Add("the stock snapshot");

        loadError = failed.Count == 0
            ? null
            : $"Some panels could not be read ({string.Join(", ", failed)}). Everything else on this page is current.";
    }

    // ── Reading a document ──────────────────────────────────────────────────

    /// <summary>
    /// Which way the stock went. SAP is asked for documents whose header names
    /// this depot on either side, so one of the two branches always holds; a
    /// document naming it on both moved stock inside the depot.
    /// </summary>
    private static Flow DirectionOf(InventoryTransferDto transfer, string depot)
    {
        var arriving = Same(transfer.ToWarehouse, depot);
        var leaving = Same(transfer.FromWarehouse, depot);

        return (arriving, leaving) switch
        {
            (true, false) => Flow.In,
            (false, true) => Flow.Out,
            _ => Flow.Internal
        };
    }

    private static string DirectionLabel(Flow flow) => flow switch
    {
        Flow.In => "In",
        Flow.Out => "Out",
        _ => "Internal"
    };

    private static string DirectionTone(Flow flow) => flow switch
    {
        Flow.In => "is-in",
        Flow.Out => "is-out",
        _ => string.Empty
    };

    private static string DirectionIcon(Flow flow) => flow switch
    {
        Flow.In => "ph-arrow-down-left",
        Flow.Out => "ph-arrow-up-right",
        _ => "ph-arrows-left-right"
    };

    private static bool Same(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private string WarehouseLabel(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "—";

        var trimmed = code.Trim();
        return warehouseNames.TryGetValue(trimmed, out var name) && !string.IsNullOrWhiteSpace(name)
            ? $"{trimmed} · {name}"
            : trimmed;
    }

    // ── Formatting ──────────────────────────────────────────────────────────

    private static string Figure(int? value) => value?.ToString("N0") ?? "—";

    private static string Percent(int part, int whole) =>
        whole <= 0 ? "0%" : $"{Math.Round(part / (decimal)whole * 100m, 2).ToString("0.##", CultureInfo.InvariantCulture)}%";

    /// <summary>
    /// A bar's height as a share of its half of the plot. Anything that moved
    /// keeps at least a 3% stub, so a one-document day on a busy axis is still
    /// something to hover.
    /// </summary>
    private static string Height(int value, int axis)
    {
        if (value <= 0 || axis <= 0) return "0";

        var share = Math.Round(value / (decimal)axis * 100m, 1);
        if (share < 3m) share = 3m;

        return share.ToString("0.#", CultureInfo.InvariantCulture) + "%";
    }

    /// <summary>
    /// The next tidy whole number at or above the busiest day. Documents are
    /// counted, not measured, so the ladder stays on integers — an axis reading
    /// 2.5 documents would be nonsense.
    /// </summary>
    private static int NiceCeiling(int value)
    {
        if (value <= 0) return 0;
        if (value <= 10) return value <= 2 ? 2 : value <= 5 ? 5 : 10;

        var magnitude = (int)Math.Pow(10, Math.Floor(Math.Log10(value)));
        return (int)Math.Ceiling(value / (double)magnitude) * magnitude;
    }

    /// <summary>
    /// SAP dates carry no time, so an age is counted in whole days from the
    /// document's date.
    /// </summary>
    private static string Age(DateTime? moment)
    {
        if (moment is not { } date) return "—";

        var days = (Today - date.Date).Days;

        return days switch
        {
            <= 0 => "today",
            1 => "yesterday",
            < 7 => $"{days}d ago",
            < 60 => $"{days / 7}w ago",
            _ => $"{days / 30}mo ago"
        };
    }

    private static string When(DateTime date) =>
        date.Date == Today ? "Today"
        : date.Date == Today.AddDays(-1) ? "Yest."
        : date.ToString("dd MMM", CultureInfo.CurrentCulture);

    /// <summary>SAP writes its document dates as plain yyyy-MM-dd strings.</summary>
    private static DateTime? ParseDocDate(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;

    private enum Flow
    {
        In,
        Out,
        Internal
    }

    private sealed record ChartBar(
        string InHeight,
        string OutHeight,
        string TipClass,
        bool IsToday,
        string Day,
        int In,
        int Out,
        string Note,
        string Label);

    private sealed record MovementRow(
        string Document,
        string DirectionLabel,
        string DirectionTone,
        string DirectionIcon,
        string Counterparty,
        string Lines,
        string When);

    private sealed record RequestRow(string Document, string Counterparty, DateTime? Raised, string Age);

    private sealed record WorkflowStep(string Number, string Title, string Body, string Href, string Cta);

    private sealed record StockHealth(
        int Total,
        int Healthy,
        int Low,
        int OutOfStock,
        int Adjusted,
        string Status,
        DateTime SnapshotDate)
    {
        public decimal InStockPercent => Total <= 0 ? 0m : (Healthy + Low) / (decimal)Total * 100m;

        public string StatusLabel => $"{SnapshotDate:dd MMM} · {Status}";

        public string StatusTone => Status.ToLowerInvariant() switch
        {
            "complete" => string.Empty,
            "failed" => "is-bad",
            _ => "is-warn"
        };
    }
}
