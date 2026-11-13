using System.Globalization;
using System.Text.Json;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using ShopInventory.Web.Components;
using ShopInventory.Web.Data;
using ShopInventory.Web.Features.Vending;
using ShopInventory.Web.Features.Vending.Queries.ExportVendingReconciliation;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

public partial class VendingReconciliation
{
    [Inject] private IVendingService VendingService { get; set; } = default!;
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private IMasterDataCacheService MasterDataCache { get; set; } = default!;

    [CascadingParameter] private Task<AuthenticationState>? AuthenticationState { get; set; }

    private enum Range { Today, Week, Month }

    private sealed record RangeOption(Range Range, string Label);

    private static readonly RangeOption[] Ranges =
    [
        new(Range.Today, "Today"),
        new(Range.Week, "7 days"),
        new(Range.Month, "Month")
    ];

    private static readonly NocturneSelectOption<string>[] StatusFilterOptions =
    [
        new(string.Empty, "Every status") { IsUnset = true, RuleAfter = true },
        new(VendingCashUpStatus.NotCashedUp, "Not cashed up", "warn"),
        new(VendingCashUpStatus.Submitted, "Awaiting approval", "accent"),
        new(VendingCashUpStatus.Queried, "Queried", "warn"),
        new(VendingCashUpStatus.Approved, "Approved", "good")
    ];

    private readonly DateTime today = IAuditService.ToCAT(DateTime.UtcNow).Date;
    private readonly CancellationTokenSource disposed = new();

    private bool isAdmin;
    private bool canCount;

    private VendingOverviewModel? overview;
    private Dictionary<string, string> partnerNames = new(StringComparer.OrdinalIgnoreCase);

    private Range range = Range.Week;
    private string? warehouseFilter;
    private string? statusFilter;

    private VendingReconciliationModel? reconciliation;
    private bool loading = true;
    private string? loadError;
    private int loadVersion;

    private bool exporting;
    private string? exportError;

    // The count being keyed or reviewed.
    private bool sheetOpen;
    private bool pickingCashier;
    private string? pickedCashier;
    private DateTime? pickedDate;
    private bool sheetLoading;
    private string? sheetError;
    private VendingCashUpSheetModel? sheet;
    private VendingCashUpForm? form;
    private string? savedSnapshot;
    private bool busy;
    private bool querying;
    private string? queryNote;
    private bool changedSinceOpen;

    protected override async Task OnInitializedAsync()
    {
        if (AuthenticationState is not null)
        {
            var user = (await AuthenticationState).User;
            isAdmin = user.IsInRole(UserRoles.Admin);
            canCount = isAdmin || user.IsInRole(UserRoles.Cashier);
        }

        try
        {
            overview = await VendingService.GetOverviewAsync(disposed.Token);
        }
        catch (InvalidOperationException)
        {
            // Only the depot filter and the cashier picker need it; the reconciliation names its own rows.
        }
        catch (OperationCanceledException)
        {
        }

        try
        {
            var codes = overview?.Depots.Select(depot => depot.BusinessPartnerCode).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
            partnerNames = (await MasterDataCache.GetBusinessPartnersAsync())
                .Where(partner => partner.CardCode is not null && codes.Contains(partner.CardCode) && !string.IsNullOrWhiteSpace(partner.CardName))
                .GroupBy(partner => partner.CardCode!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().CardName!, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
        }

        await ReloadAsync();
    }

    public void Dispose()
    {
        disposed.Cancel();
        disposed.Dispose();
    }

    // ── The reconciliation ─────────────────────────────────────────────────

    private (DateTime From, DateTime To) Window => range switch
    {
        Range.Today => (today, today),
        Range.Week => (today.AddDays(-6), today),
        _ => (new DateTime(today.Year, today.Month, 1), today)
    };

    private async Task ReloadAsync()
    {
        var version = ++loadVersion;
        loading = true;
        loadError = null;
        var (from, to) = Window;

        try
        {
            var (read, error) = await VendingService.GetReconciliationAsync(from, to, warehouseFilter, disposed.Token);
            if (version != loadVersion)
            {
                return;
            }

            if (read is null)
            {
                loadError = error ?? "The cash-ups could not be loaded.";
                return;
            }

            reconciliation = read;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (version == loadVersion)
            {
                loading = false;
            }
        }
    }

    private async Task SetRangeAsync(Range value)
    {
        if (range == value)
        {
            return;
        }

        range = value;
        await ReloadAsync();
    }

    private List<VendingReconciliationRowModel> VisibleRows =>
        (reconciliation?.Rows ?? [])
            .Where(row => string.IsNullOrEmpty(statusFilter) || row.Status == statusFilter)
            .ToList();

    private List<VendingReconciliationRowModel> ReviewRows =>
        (reconciliation?.Rows ?? [])
            .Where(row => row.Status is VendingCashUpStatus.Submitted or VendingCashUpStatus.Queried)
            .OrderBy(row => row.Status == VendingCashUpStatus.Submitted ? 0 : 1)
            .ThenByDescending(row => row.Currencies.Any(currency => currency.Variance is < 0m))
            .ThenByDescending(row => row.BusinessDate)
            .ToList();

    private sealed record RecentCount(string Key, string Label, string Currency, decimal? Variance, string Width);

    /// <summary>
    /// The last six counts' variances, one bar per count and currency. Each bar is scaled against the
    /// largest variance in its own currency, so a ZiG figure is never measured against a USD one.
    /// </summary>
    private List<RecentCount> RecentCounts
    {
        get
        {
            var counted = (reconciliation?.Rows ?? [])
                .Where(row => row.IsCounted)
                .OrderByDescending(row => row.BusinessDate)
                .ThenByDescending(row => row.CapturedAt)
                .Take(6)
                .SelectMany(row => row.Currencies.Select(currency => (Row: row, Currency: currency)))
                .ToList();

            var largest = counted
                .GroupBy(pair => pair.Currency.Currency)
                .ToDictionary(group => group.Key, group => group.Max(pair => Math.Abs(pair.Currency.Variance ?? 0m)));

            return counted
                .Select(pair =>
                {
                    var max = largest[pair.Currency.Currency];
                    var width = max == 0m ? 0m : Math.Max(4m, Math.Abs(pair.Currency.Variance ?? 0m) / max * 100m);
                    return new RecentCount(
                        $"{RowKey(pair.Row)}|{pair.Currency.Currency}",
                        $"{pair.Row.BusinessDate.ToString("ddd d", CultureInfo.InvariantCulture)} · {pair.Row.CashierName}",
                        pair.Currency.Currency,
                        pair.Currency.Variance,
                        $"{width.ToString("0.#", CultureInfo.InvariantCulture)}%");
                })
                .ToList();
        }
    }

    private int RecentCountTotal => RecentCounts.Select(item => item.Key.Split('|')[0..3]).Select(parts => string.Join('|', parts)).Distinct().Count();

    private string RecentSummary
    {
        get
        {
            var counted = (reconciliation?.Rows ?? []).Where(row => row.IsCounted).ToList();
            var balanced = counted.Count(row => row.Balanced);
            return balanced == counted.Count
                ? $"Every count {RangeWords} balanced."
                : $"{balanced:N0} of {counted.Count:N0} counts {RangeWords} balanced.";
        }
    }

    private List<VendingReconciliationTotalModel> Totals => reconciliation?.Totals ?? [];

    private VendingReconciliationCountsModel Counts => reconciliation?.Counts ?? new();

    private int CountedDays => Counts.CashierDays - Counts.NotCashedUp;

    private decimal OpenShortfallTotal => Totals.Sum(total => total.OpenShortfall);

    /// <summary>The first currency short as the figure; any other currency short goes in the note.</summary>
    private string OpenShortfallText =>
        Totals.FirstOrDefault(total => total.OpenShortfall > 0m) is { } first ? Money(first.Currency, first.OpenShortfall) : "None";

    private string? OpenShortfallOthers
    {
        get
        {
            var others = Totals.Where(total => total.OpenShortfall > 0m).Skip(1).Select(total => Money(total.Currency, total.OpenShortfall)).ToList();
            return others.Count == 0 ? null : string.Join(", ", others);
        }
    }

    private string BankedNote
    {
        get
        {
            var others = Totals.Skip(1).Where(total => total.Banked > 0m).Select(total => Money(total.Currency, total.Banked)).ToList();
            var counted = $"{CountedDays:N0} of {Plural(Counts.CashierDays, "cashier-day")} counted";
            return others.Count == 0 ? counted : $"+ {string.Join(", ", others)} · {counted}";
        }
    }

    private string ResultLabel => reconciliation is null
        ? string.Empty
        : string.IsNullOrEmpty(statusFilter)
            ? Plural(reconciliation.Rows.Count, "cashier-day")
            : $"{VisibleRows.Count:N0} of {Plural(reconciliation.Rows.Count, "cashier-day")}";

    private string RangeWords => range switch
    {
        Range.Today => "today",
        Range.Week => "in the last 7 days",
        _ => "this month"
    };

    // A figure is a dash until the range has loaded, never a zero: "0 not cashed up" is a claim.
    private string Figure(Func<string> value) => reconciliation is null ? "—" : value();

    private List<NocturneSelectOption<string>> DepotFilterOptions =>
    [
        new(string.Empty, "All depots") { IsUnset = true, RuleAfter = true },
        .. (overview?.Depots ?? [])
            .SelectMany(depot => depot.WarehouseCodes.Select(warehouse => (depot.BusinessPartnerCode, Warehouse: warehouse)))
            .DistinctBy(pair => pair.Warehouse, StringComparer.OrdinalIgnoreCase)
            .OrderBy(pair => DepotName(pair.BusinessPartnerCode), StringComparer.OrdinalIgnoreCase)
            .Select(pair => new NocturneSelectOption<string>(pair.Warehouse, $"{DepotName(pair.BusinessPartnerCode)} · {pair.Warehouse}"))
    ];

    private string DepotName(string? partnerCode) =>
        partnerCode is not null && partnerNames.TryGetValue(partnerCode, out var name) ? name : partnerCode ?? "No depot";

    private async Task ExportAsync()
    {
        exporting = true;
        exportError = null;
        try
        {
            var (from, to) = Window;
            var scope = string.IsNullOrEmpty(warehouseFilter)
                ? "all depots"
                : DepotFilterOptions.FirstOrDefault(option => option.Value == warehouseFilter)?.Label ?? warehouseFilter;
            var report = await Mediator.Send(new ExportVendingReconciliationQuery(from, to, warehouseFilter, scope), disposed.Token);
            if (report.IsError)
            {
                exportError = report.FirstError.Description;
                return;
            }

            await JS.InvokeVoidAsync("downloadFile", report.Value.FileName,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                Convert.ToBase64String(report.Value.Content));
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            exporting = false;
        }
    }

    // ── The count ──────────────────────────────────────────────────────────

    private List<NocturneSelectOption<string>> CashierOptions =>
        (overview?.Accounts ?? [])
            .Where(account => account.IsActive && !string.IsNullOrWhiteSpace(account.WarehouseCode))
            .OrderBy(account => DepotName(account.BusinessPartnerCode), StringComparer.OrdinalIgnoreCase)
            .ThenBy(account => account.FullName ?? account.Username, StringComparer.OrdinalIgnoreCase)
            .Select(account => new NocturneSelectOption<string>(
                account.UserId.ToString(),
                $"{account.FullName ?? account.Username} · {DepotName(account.BusinessPartnerCode)}"))
            .ToList();

    private void OpenNewCashUp()
    {
        ResetSheet();
        sheetOpen = true;
        pickingCashier = true;
        pickedDate = today;
    }

    private async Task OpenSheetAsync(Guid cashierUserId, string warehouseCode, DateTime businessDate)
    {
        ResetSheet();
        sheetOpen = true;
        await LoadSheetAsync(cashierUserId, warehouseCode, businessDate);
    }

    private async Task OnPickedDateChanged(DateTime? value)
    {
        pickedDate = value;
        await LoadPickedSheetAsync();
    }

    private async Task LoadPickedSheetAsync()
    {
        sheet = null;
        form = null;
        sheetError = null;
        if (!Guid.TryParse(pickedCashier, out var cashierId) || pickedDate is not { } day)
        {
            return;
        }

        if (day.Date > today)
        {
            sheetError = "A day that has not happened yet cannot be cashed up.";
            return;
        }

        var account = overview?.Accounts.FirstOrDefault(candidate => candidate.UserId == cashierId);
        if (account?.WarehouseCode is not { Length: > 0 } warehouse)
        {
            sheetError = "This cashier has no warehouse, so there are no sales to count against.";
            return;
        }

        await LoadSheetAsync(cashierId, warehouse, day.Date);
    }

    private async Task LoadSheetAsync(Guid cashierUserId, string warehouseCode, DateTime businessDate)
    {
        sheetLoading = true;
        sheetError = null;
        try
        {
            var (read, error) = await VendingService.GetCashUpSheetAsync(cashierUserId, warehouseCode, businessDate, disposed.Token);
            if (read is null)
            {
                sheetError = error ?? "The cash-up could not be loaded.";
                return;
            }

            Show(read);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            sheetLoading = false;
        }
    }

    private void Show(VendingCashUpSheetModel read)
    {
        sheet = read;
        form = VendingCashUpForm.From(read);
        savedSnapshot = Snapshot();
        querying = false;
        queryNote = null;
    }

    private string? Snapshot() =>
        sheet is null || form is null ? null : JsonSerializer.Serialize(form.ToRequest(sheet));

    private bool IsDirty => form is not null && Snapshot() != savedSnapshot;

    /// <summary>
    /// Nothing about an approved count can change, and a Manager only reads. Either way the form is
    /// shown, so the figures behind a decision stay readable.
    /// </summary>
    private bool ReadOnly => !canCount || sheet is null || sheet.Status == VendingCashUpStatus.Approved;

    private bool CanDecide => isAdmin && sheet is { CashUpId: not null, Status: VendingCashUpStatus.Submitted };

    private bool CanApprove =>
        CanDecide && sheet is { SavedByCaller: false, SalesChangedSinceCount: false, NeedsReason: false } && !IsDirty;

    /// <summary>Why Approve is greyed out, in the order the API would refuse it.</summary>
    private string? ApprovalHint
    {
        get
        {
            if (!CanDecide || sheet is null)
            {
                return sheet is { Status: VendingCashUpStatus.Submitted } && !isAdmin
                    ? "An Admin who did not key this count approves it."
                    : null;
            }

            if (sheet.SavedByCaller)
            {
                return "You keyed or edited this count, so another Admin has to approve it.";
            }

            if (IsDirty)
            {
                return "Save the changes before approving.";
            }

            if (sheet.SalesChangedSinceCount)
            {
                return "Sales changed since the count. Save it again before approving.";
            }

            if (sheet.NeedsReason)
            {
                return "The count is off and has no reason. Record one and save before approving.";
            }

            return null;
        }
    }

    private async Task SaveAsync()
    {
        if (sheet is null || form is null)
        {
            return;
        }

        busy = true;
        sheetError = null;
        try
        {
            var (saved, error) = await VendingService.SaveCashUpAsync(form.ToRequest(sheet), disposed.Token);
            if (saved is null)
            {
                sheetError = error;
                return;
            }

            Show(saved);
            pickingCashier = false;
            changedSinceOpen = true;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            busy = false;
        }
    }

    private Task ApproveAsync() => DecideAsync(approve: true);

    private Task SendQueryAsync() => DecideAsync(approve: false);

    private async Task DecideAsync(bool approve)
    {
        if (sheet?.CashUpId is not { } id)
        {
            return;
        }

        busy = true;
        sheetError = null;
        try
        {
            var (decided, error) = await VendingService.DecideCashUpAsync(id, sheet.Revision, approve, approve ? null : queryNote, disposed.Token);
            if (decided is null)
            {
                sheetError = error;
                return;
            }

            Show(decided);
            changedSinceOpen = true;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            busy = false;
        }
    }

    private async Task CloseSheet()
    {
        if (busy)
        {
            return;
        }

        var reload = changedSinceOpen;
        sheetOpen = false;
        ResetSheet();
        if (reload)
        {
            await ReloadAsync();
        }
    }

    private void ResetSheet()
    {
        pickingCashier = false;
        pickedCashier = null;
        pickedDate = null;
        sheet = null;
        form = null;
        sheetError = null;
        savedSnapshot = null;
        querying = false;
        queryNote = null;
        changedSinceOpen = false;
    }

    private string SheetTitle => sheet is null
        ? "Open cash-up"
        : $"{sheet.CashierName} · {sheet.BusinessDate.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture)}";

    private string SheetTrail
    {
        get
        {
            if (sheet is null)
            {
                return string.Empty;
            }

            var depot = $"{sheet.DepotName ?? sheet.BusinessPartnerCode ?? "No depot"} · {sheet.WarehouseCode}";
            if (sheet.CashUpId is null)
            {
                return $"{depot} · not counted yet";
            }

            var parts = new List<string> { depot, $"counted by {sheet.CapturedByName} {When(sheet.CapturedAt)}" };
            if (sheet.UpdatedByName is not null && sheet.UpdatedAt != sheet.CapturedAt)
            {
                parts.Add($"last saved by {sheet.UpdatedByName} {When(sheet.UpdatedAt)}");
            }

            if (sheet.ApprovedByName is not null)
            {
                parts.Add($"approved by {sheet.ApprovedByName} {When(sheet.ApprovedAt)}");
            }

            return string.Join(" · ", parts);
        }
    }

    private static int? NoteQuantity(VendingCashUpForm.CurrencyForm currency, decimal value) =>
        currency.Notes.TryGetValue(value, out var quantity) ? quantity : null;

    private static void SetNoteQuantity(VendingCashUpForm.CurrencyForm currency, decimal value, object? raw)
    {
        currency.Notes[value] = int.TryParse(raw?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var quantity) && quantity >= 0
            ? quantity
            : null;
    }

    // ── Formatting ─────────────────────────────────────────────────────────

    private static string RowKey(VendingReconciliationRowModel row) =>
        $"{row.CashierUserId}|{row.WarehouseCode}|{row.BusinessDate:yyyyMMdd}";

    public static string CurrencyLabel(string currency) => currency == "ZIG" ? "ZiG" : currency;

    public static string Money(string currency, decimal amount) =>
        $"{CurrencyLabel(currency)} {amount.ToString("N2", CultureInfo.InvariantCulture)}";

    private static string MoneyList(IEnumerable<(string Currency, decimal Amount)> amounts, bool primaryOnly)
    {
        var list = amounts.ToList();
        if (list.Count == 0)
        {
            return "—";
        }

        return primaryOnly ? Money(list[0].Currency, list[0].Amount) : string.Join(" · ", list.Select(item => Money(item.Currency, item.Amount)));
    }

    /// <summary>A dash for a figure nobody counted; "Balanced" for nothing off; signed otherwise.</summary>
    public static string VarianceText(string currency, decimal? variance) => variance switch
    {
        null => "—",
        0m => "Balanced",
        < 0m => $"− {Money(currency, -variance.Value)}",
        _ => $"+ {Money(currency, variance.Value)}"
    };

    public static string VarianceTone(decimal? variance) => variance switch
    {
        null => "is-none",
        0m => "is-good",
        < 0m => "is-bad",
        _ => "is-over"
    };

    public static string StatusLabel(string status) => ReportExportService.VendingReconciliationStatusText(status);

    public static string StatusChip(string status) => status switch
    {
        VendingCashUpStatus.Approved => "shop-chip-good",
        VendingCashUpStatus.Submitted => "vend-rec-chip-accent",
        VendingCashUpStatus.Queried => "vend-rec-chip-warn",
        _ => "vend-rec-chip-warn"
    };

    private static string StatusWords(string? status) => status switch
    {
        VendingCashUpStatus.NotCashedUp => "is waiting for a count",
        VendingCashUpStatus.Submitted => "is awaiting approval",
        VendingCashUpStatus.Queried => "is queried",
        VendingCashUpStatus.Approved => "is approved",
        _ => "matches"
    };

    private static string Plural(int count, string noun) =>
        $"{count.ToString("N0", CultureInfo.InvariantCulture)} {noun}{(count == 1 ? string.Empty : "s")}";

    private static string When(DateTime? at) =>
        at is { } value ? value.ToString("d MMM HH:mm", CultureInfo.InvariantCulture) : string.Empty;
}
