using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using ShopInventory.Web.Common;
using ShopInventory.Web.Components.Dashboard;
using ShopInventory.Web.Data;
using ShopInventory.Web.Features.CreditNoteApprovals.Queries.GetCreditNoteApprovals;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

/// <summary>
/// The wash bay's dashboard at /washbay-dashboard.
///
/// Every figure is nullable until its read lands, so the page shows "—" rather than a zero that is
/// about to jump. Each read is caught separately so one dead service cannot blank the page, and the
/// credit note reads — live from SAP, and slow — never hold up the transfer panels.
/// </summary>
public partial class WashBayDashboard
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private IInventoryTransferService TransferService { get; set; } = default!;
    [Inject] private IMasterDataCacheService MasterData { get; set; } = default!;
    [Inject] private IUserManagementService UserManagement { get; set; } = default!;
    [Inject] private IAuditService AuditService { get; set; } = default!;
    [Inject] private ILogger<WashBayDashboard> Logger { get; set; } = default!;

    [CascadingParameter] private Task<AuthenticationState>? AuthTask { get; set; }

    /// <summary>Rows drawn in each panel.</summary>
    private const int RowsShown = 6;

    private string? warehouse;
    private List<NocturneSelectOption<string>> warehouseOptions = [];
    private Dictionary<string, string> warehouseNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Bumped on every warehouse change so late replies can be dropped.</summary>
    private int dataVersion;

    private DateTime? loadedAt;

    private int? pendingCreditCount;
    private int? approvedCreditCount;
    private string? creditError;
    private List<CreditNoteApprovalListItemDto>? waitingCredits;
    private bool isLoadingCredits = true;

    private int? pendingTransferCount;
    private List<PendingInventoryTransferDto>? pendingTransfers;
    private bool isLoadingTransfers = true;

    private string currentUsername = "there";
    private bool _initialized;

    protected override async Task OnParametersSetAsync()
    {
        using var pageReads = PageReads.Bind(PageLifetime);
        if (AuthTask is null || _initialized) return;

        var authState = await AuthTask;
        var user = authState.User;

        if (user.Identity?.IsAuthenticated != true) return;

        _initialized = true;
        currentUsername = user.Identity?.Name ?? currentUsername;

        // The credit notes do not depend on the warehouse, so they start before it is resolved.
        var credits = LoadCreditNotesAsync();
        await ResolveWarehouseAsync(user.FindAll("warehouse").Select(claim => claim.Value));
        await Task.WhenAll(credits, LoadTransfersAsync(dataVersion));

        loadedAt = DateTime.Now;
        await AuditService.LogAsync(AuditActions.ViewDashboard, "WashBayDashboard", null);
    }

    /// <summary>
    /// The warehouses on the account's claims, named from the master-data cache. The wash bay is
    /// assigned its warehouse, so an account with no claim shows no transfers rather than every
    /// warehouse's.
    /// </summary>
    private async Task ResolveWarehouseAsync(IEnumerable<string> claimValues)
    {
        var assigned = claimValues
            .Select(value => value.Trim())
            .Where(code => code.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        try
        {
            var warehouses = await MasterData.GetWarehousesAsync();
            warehouseNames = warehouses
                .Where(item => !string.IsNullOrWhiteSpace(item.WarehouseCode))
                .GroupBy(item => item.WarehouseCode!.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().WarehouseName ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            // The codes on the claims are enough to run every read on this page; only the names would be missing.
            Logger.LogWarning(ex, "Could not load warehouses for the wash bay dashboard.");
        }

        warehouseOptions = assigned
            .Select(code => new NocturneSelectOption<string>(code, NameFor(code)) { Hint = code })
            .ToList();

        if (assigned.Count == 0)
        {
            return;
        }

        var me = await UserManagement.GetCurrentUserAsync();
        warehouse = HomeDepotResolver.Resolve(assigned, warehouseNames, me?.AssignedSection);
    }

    private async Task OnWarehouseChangedAsync()
    {
        dataVersion++;
        isLoadingTransfers = true;
        await LoadTransfersAsync(dataVersion);
    }

    /// <summary>
    /// Two reads of SAP's queue side by side: what waits for a decision, and what is approved but not
    /// yet added. Each carries its own total, so the figures and the panel come from the same two calls.
    /// </summary>
    private async Task LoadCreditNotesAsync()
    {
        try
        {
            var pendingTask = Mediator.Send(new GetCreditNoteApprovalsQuery("pending", 1, RowsShown));
            var approvedTask = Mediator.Send(new GetCreditNoteApprovalsQuery("approved", 1, RowsShown));
            await Task.WhenAll(pendingTask, approvedTask);

            var pending = await pendingTask;
            var approved = await approvedTask;

            if (pending.IsError || approved.IsError)
            {
                creditError = (pending.IsError ? pending.FirstError : approved.FirstError).Description;
            }

            pendingCreditCount = pending.IsError ? null : pending.Value.TotalCount;
            approvedCreditCount = approved.IsError ? null : approved.Value.TotalCount;

            // Approved first, because adding them is the step that finishes the job; then oldest first.
            waitingCredits = (approved.IsError ? [] : approved.Value.Items)
                .OrderBy(item => item.CreatedDate)
                .Concat((pending.IsError ? [] : pending.Value.Items).OrderBy(item => item.CreatedDate))
                .Take(RowsShown)
                .ToList();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Wash bay dashboard could not read the credit note approval queue.");
            creditError = "The held credit notes could not be read from SAP.";
        }
        finally
        {
            isLoadingCredits = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task LoadTransfersAsync(int version)
    {
        if (warehouse is null)
        {
            isLoadingTransfers = false;
            return;
        }

        try
        {
            var response = await TransferService.GetPendingTransfersAsync(
                PendingTransferStatuses.AwaitingApproval, warehouseCode: warehouse, pageSize: RowsShown);

            if (version != dataVersion) return;

            pendingTransferCount = response?.TotalCount;

            // Oldest first: the top of the queue should be what has waited longest.
            pendingTransfers = response?.Items
                .OrderBy(transfer => transfer.CreatedAtUtc)
                .Take(RowsShown)
                .ToList();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Wash bay dashboard could not read the transfer queue for {Warehouse}.", warehouse);
        }
        finally
        {
            if (version == dataVersion)
            {
                isLoadingTransfers = false;
            }

            await InvokeAsync(StateHasChanged);
        }
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

    private static StatTone WaitingTone(int? count) => count switch
    {
        null => StatTone.Neutral,
        0 => StatTone.Ok,
        _ => StatTone.Warn
    };

    private static string? CreditNote(int? count, string? error) => error is not null && count is null
        ? "Could not read SAP"
        : count switch
        {
            null => null,
            0 => "Queue is clear",
            _ => "Waiting on you"
        };

    private string NameFor(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "—";

        return warehouseNames.TryGetValue(code.Trim(), out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : code.Trim();
    }

    /// <summary>"Name · CODE", or the code alone when there is no name for it.</summary>
    private string WarehouseLabel
    {
        get
        {
            if (warehouse is null) return "Wash bay";

            var name = NameFor(warehouse);
            return string.Equals(name, warehouse, StringComparison.OrdinalIgnoreCase) ? warehouse : $"{name} · {warehouse}";
        }
    }

    private static string? WaitingNote(int? count) => count switch
    {
        null => null,
        0 => "Queue is clear",
        _ => "Waiting on you"
    };

    private static string Money(CreditNoteApprovalListItemDto request) =>
        $"{request.DocCurrency} {request.DocTotal:N2}".Trim();

    private static string Route(PendingInventoryTransferDto transfer) =>
        $"{transfer.FromWarehouse ?? "—"} → {transfer.ToWarehouse ?? "—"}";

    private static string Age(DateTime? created) => created is { } value
        ? Age(value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime())
        : "—";

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

    /// <summary>The first name to greet by, taken from the username.</summary>
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
