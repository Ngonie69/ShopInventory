using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using ShopInventory.Web.Common;
using ShopInventory.Web.Components.Dashboard;
using ShopInventory.Web.Data;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

/// <summary>
/// /stock-in-hand: a warehouse's stock now, or at any moment since its ledger began.
/// </summary>
/// <remarks>
/// <para><b>Times are CAT.</b> What the user picks is a wall-clock time at the shop; it is turned
/// into UTC once, here, before it reaches the API.</para>
///
/// <para><b>The URL carries the moment</b> (<c>?warehouse=KEFSHOP&amp;at=2026-11-17T17:30</c>), so a
/// link to "what we held at last night's close" can be sent to someone and opens on that moment.</para>
/// </remarks>
public partial class StockInHand
{
    [Inject] private IWarehouseStockCacheService StockCache { get; set; } = default!;
    [Inject] private IStockHistoryService StockHistory { get; set; } = default!;
    [Inject] private IMasterDataCacheService MasterData { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [Inject] private IReportExportService ExportService { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private ILogger<StockInHand> Logger { get; set; } = default!;

    [CascadingParameter] private Task<AuthenticationState>? AuthTask { get; set; }

    [SupplyParameterFromQuery(Name = "warehouse")] public string? WarehouseQuery { get; set; }

    /// <summary>A CAT wall-clock time, yyyy-MM-ddTHH:mm.</summary>
    [SupplyParameterFromQuery(Name = "at")] public string? AtQuery { get; set; }

    private enum Moment { Now, Opening, LastClose, SameTimeYesterday, Custom }

    private sealed record Preset(Moment Mode, string Label);

    private static readonly Preset[] Presets =
    [
        new(Moment.Now, "Now"),
        new(Moment.Opening, "Today's opening"),
        new(Moment.LastClose, "Last night's close"),
        new(Moment.SameTimeYesterday, "This time yesterday")
    ];

    private static readonly NocturneSelectOption<string>[] TimeOptions = Enumerable.Range(0, 48)
        .Select(half => TimeSpan.FromMinutes(half * 30).ToString(@"hh\:mm"))
        .Select(text => new NocturneSelectOption<string>(text, text))
        .ToArray();

    private sealed record Filter(string Key, string Label);

    private string? warehouse;
    private List<NocturneSelectOption<string>> warehouseOptions = [];
    private Dictionary<string, string> warehouseNames = new(StringComparer.OrdinalIgnoreCase);

    private Moment mode = Moment.Now;
    private DateTime? customDate;
    private string? customTime = "12:00";

    /// <summary>The moment on screen, UTC. Null for Now.</summary>
    private DateTime? asAtUtc;

    private int loadVersion;
    private bool isLoading = true;
    private string? loadError;
    private bool recorded = true;
    private DateTime? recordedFrom;
    private DateTime? loadedAt;
    private List<StockInHandRow> rows = [];

    private string search = string.Empty;
    private string view = "all";

    private string? selectedCode;
    private StockInHandRow? selected;
    private bool isLoadingDetail;
    private string? detailError;
    private string? detailNote;
    private decimal detailStart;
    private DateTime detailFromUtc;
    private List<DetailEntry> detail = [];

    private bool isExporting;
    private string? exportError;

    private bool _initialized;

    private sealed record DetailEntry(string When, string Label, string? Reference, string QtyText, string QtyClass, decimal Balance, bool IsOpening);

    protected override async Task OnParametersSetAsync()
    {
        using var pageReads = PageReads.Bind(PageLifetime);
        if (AuthTask is null || _initialized) return;

        var user = (await AuthTask).User;
        if (user.Identity?.IsAuthenticated != true) return;
        _initialized = true;

        await ResolveWarehouseAsync(user.FindAll("warehouse").Select(claim => claim.Value));
        ReadMomentFromQuery();
        await LoadAsync();
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
            Logger.LogWarning(ex, "Could not load warehouses for stock in hand.");
        }

        warehouseNames = all
            .Where(item => !string.IsNullOrWhiteSpace(item.WarehouseCode))
            .GroupBy(item => item.WarehouseCode!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().WarehouseName ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);

        // No claim is unrestricted rather than unassigned, the reading every warehouse page takes.
        var codes = assigned.Count > 0
            ? assigned
            : all.Where(item => item.IsActive && !string.IsNullOrWhiteSpace(item.WarehouseCode))
                .Select(item => item.WarehouseCode!.Trim())
                .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                .ToList();

        warehouseOptions = codes
            .Select(code => new NocturneSelectOption<string>(code, NameFor(code)) { Hint = code })
            .ToList();

        // A warehouse named in the URL is honoured only when the account may see it.
        warehouse = codes.FirstOrDefault(code => code.Equals(WarehouseQuery?.Trim(), StringComparison.OrdinalIgnoreCase))
                    ?? codes.FirstOrDefault(code => code.Equals("KEFSHOP", StringComparison.OrdinalIgnoreCase))
                    ?? codes.FirstOrDefault();
    }

    private void ReadMomentFromQuery()
    {
        if (string.IsNullOrWhiteSpace(AtQuery)) return;
        if (!DateTime.TryParseExact(AtQuery.Trim(), "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var cat)) return;

        mode = Moment.Custom;
        customDate = cat.Date;
        customTime = cat.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (!TimeOptions.Any(option => option.Value == customTime))
        {
            // A link may name a minute the half-hour list does not offer; keep it exactly.
            asAtUtc = IAuditService.ToUTC(cat);
            return;
        }

        asAtUtc = IAuditService.ToUTC(cat);
    }

    private async Task PickPresetAsync(Moment preset)
    {
        var now = DateTime.UtcNow;
        mode = preset;
        asAtUtc = preset switch
        {
            Moment.Now => null,
            Moment.Opening => StockDayClock.OpeningUtc(now),
            // A second before this morning's fetch is the last moment of yesterday's ledger day.
            Moment.LastClose => StockDayClock.OpeningUtc(now).AddSeconds(-1),
            Moment.SameTimeYesterday => now.AddDays(-1),
            _ => asAtUtc
        };

        WriteMomentToUrl();
        await LoadAsync();
    }

    private async Task ShowCustomAsync()
    {
        if (customDate is null) return;

        var time = TimeSpan.TryParse(customTime, CultureInfo.InvariantCulture, out var parsed) ? parsed : TimeSpan.Zero;
        var cat = customDate.Value.Date + time;
        var utc = IAuditService.ToUTC(cat);

        mode = utc >= DateTime.UtcNow ? Moment.Now : Moment.Custom;
        asAtUtc = mode == Moment.Now ? null : utc;
        WriteMomentToUrl();
        await LoadAsync();
    }

    private async Task OnWarehouseChangedAsync()
    {
        selectedCode = null;
        selected = null;
        WriteMomentToUrl();
        await LoadAsync();
    }

    private void WriteMomentToUrl()
    {
        var parameters = new Dictionary<string, object?>
        {
            ["warehouse"] = warehouse,
            ["at"] = asAtUtc is { } utc
                ? IAuditService.ToCAT(utc).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture)
                : null
        };
        Navigation.NavigateTo(Navigation.GetUriWithQueryParameters(parameters), replace: true);
    }

    private async Task LoadAsync()
    {
        if (warehouse is null)
        {
            isLoading = false;
            return;
        }

        var version = ++loadVersion;
        var code = warehouse;
        isLoading = true;
        loadError = null;
        StateHasChanged();

        try
        {
            var live = (await StockCache.GetAllCachedStockAsync(code))?.Products ?? [];
            if (version != loadVersion) return;

            if (asAtUtc is not { } at)
            {
                rows = StockInHandRows.FromLive(live);
                recorded = true;
            }
            else
            {
                var thenTask = StockHistory.GetStockAsAtAsync(code, at);
                var nowTask = StockHistory.GetStockAsAtAsync(code, DateTime.UtcNow);
                await Task.WhenAll(thenTask, nowTask);
                if (version != loadVersion) return;

                var (ok, message, then) = thenTask.Result;
                if (!ok || then is null)
                {
                    loadError = message;
                    rows = [];
                    return;
                }

                recorded = then.Recorded;
                recordedFrom = then.RecordedFrom;
                rows = then.Recorded
                    ? StockInHandRows.FromLedger(then, nowTask.Result.Value, live)
                    : [];
            }

            loadedAt = DateTime.Now;

            if (selectedCode is not null)
            {
                selected = rows.FirstOrDefault(row => row.Code.Equals(selectedCode, StringComparison.OrdinalIgnoreCase));
                if (selected is not null) await LoadDetailAsync();
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Stock in hand could not be read for {Warehouse}.", code);
            loadError = "The stock could not be read.";
        }
        finally
        {
            if (version == loadVersion) isLoading = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task SelectAsync(string itemCode)
    {
        selectedCode = itemCode;
        selected = rows.FirstOrDefault(row => row.Code.Equals(itemCode, StringComparison.OrdinalIgnoreCase));
        if (selected is not null) await LoadDetailAsync();
    }

    private async Task LoadDetailAsync()
    {
        if (warehouse is null || selected is null) return;

        var item = selected;
        var now = DateTime.UtcNow;
        var from = asAtUtc ?? StockDayClock.OpeningUtc(now);
        detailNote = null;

        // The API answers at most a month at a time; an older moment shows its last month.
        var maxWindow = TimeSpan.FromDays(31);
        if (now - from > maxWindow)
        {
            from = now - maxWindow;
            detailNote = "Showing the last 31 days of movements; the figure at the moment picked is still exact.";
        }

        detailFromUtc = from;
        isLoadingDetail = true;
        detailError = null;
        StateHasChanged();

        try
        {
            var (ok, message, value) = await StockHistory.GetMovementsAsync(warehouse, from, now, item.Code);
            if (!ReferenceEquals(item, selected)) return;

            if (!ok || value is null)
            {
                detailError = message;
                detail = [];
                return;
            }

            detailStart = asAtUtc is null || detailNote is not null
                ? StockInHandRows.StartBalance(value, item.Then)
                : item.Then;

            detail = StockInHandRows.Ledger(value)
                .Select(entry => new DetailEntry(
                    When(entry.AtUtc),
                    entry.Label,
                    entry.Reference,
                    entry.Quantity is { } q ? Signed(q) : string.Empty,
                    entry.Quantity switch { > 0 => "sih-pos", < 0 => "sih-out", _ => string.Empty },
                    entry.Balance,
                    entry.IsOpening))
                .ToList();

            if (value.Truncated)
                detailNote = "More movements fell in this window than the page shows; pick a later moment to see the rest.";
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Stock in hand could not read movements for {Item}.", item.Code);
            detailError = "The movements could not be read.";
        }
        finally
        {
            if (ReferenceEquals(item, selected)) isLoadingDetail = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    /// <summary>Whether there is a read on screen to export: rows of the moment shown, not a load in flight.</summary>
    private bool CanExport => !isLoading && loadError is null && recorded && warehouse is not null && rows.Count > 0;

    /// <summary>
    /// Every item of the moment on screen, whatever chip or search narrows the table: the workbook's own
    /// Status column and filter narrow it again.
    /// </summary>
    private async Task ExportAsync()
    {
        if (!CanExport || isExporting) return;

        isExporting = true;
        exportError = null;
        StateHasChanged();
        try
        {
            var export = new StockInHandExport(warehouse!, NameFor(warehouse), asAtUtc, rows.ToList());
            var bytes = await Task.Run(() => ExportService.ExportStockInHandToExcel(export));
            var stamp = asAtUtc is { } at ? IAuditService.ToCAT(at) : IAuditService.ToCAT(DateTime.UtcNow);
            await JS.InvokeVoidAsync(
                "downloadFile",
                $"Stock_in_Hand_{warehouse}_{stamp:yyyyMMdd_HHmm}.xlsx",
                Convert.ToBase64String(bytes));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Could not export stock in hand for {Warehouse}.", warehouse);
            exportError = "The workbook could not be made. Try again in a moment.";
        }
        finally
        {
            isExporting = false;
            StateHasChanged();
        }
    }

    // ── Presentation ────────────────────────────────────────────────────────

    private List<StockInHandRow> VisibleRows => rows
        .Where(row => Matches(row, ActiveView))
        .Where(row => string.IsNullOrWhiteSpace(search)
                      || row.Name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
                      || row.Code.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase))
        .ToList();

    private IEnumerable<Filter> Filters => mode == Moment.Now
        ? [new("all", "All"), new("available", "Available"), new("stock", "On the shelf"), new("zero", "At zero"), new("oversold", "Oversold")]
        : [new("all", "All"), new("stock", "On the shelf then"), new("zero", "At zero then"), new("changed", "Changed since")];

    /// <summary>
    /// The chip in force. A chip the moment does not offer (Available or Oversold after picking a
    /// past moment, Changed since after going back to Now) falls back to All rather than filtering
    /// with nothing lit.
    /// </summary>
    private string ActiveView => Filters.Any(filter => filter.Key == view) ? view : "all";

    private bool Matches(StockInHandRow row, string key) => key switch
    {
        // Free to sell above zero: on hand that is not already committed. Now only, since
        // committed is not kept historically.
        "available" => row.Free > 0,
        "stock" => (mode == Moment.Now ? row.Now : row.Then) > 0,
        "zero" => (mode == Moment.Now ? row.Now : row.Then) <= 0,
        "oversold" => row.Free < 0,
        "changed" => row.Change != 0,
        _ => true
    };

    private int CountFor(string key) => rows.Count(row => Matches(row, key));

    private sealed record SummaryFigure(string Label, int? Value, string Note, StatTone Tone, string Icon, bool Lead);

    private IEnumerable<SummaryFigure> Summary
    {
        get
        {
            int? Count(Func<StockInHandRow, bool> predicate) => isLoading || !recorded ? null : rows.Count(predicate);

            if (mode == Moment.Now)
            {
                var oversold = Count(row => row.Free < 0);
                return
                [
                    new("On the shelf", Count(row => row.Now > 0), "Items with stock on hand", StatTone.Neutral, "box", true),
                    new("At zero", Count(row => row.Now <= 0), "Nothing on hand", Count(row => row.Now <= 0) > 0 ? StatTone.Warn : StatTone.Ok, "warning", false),
                    new("Oversold", oversold, "Committed above on hand", oversold > 0 ? StatTone.Critical : StatTone.Ok, "clipboard-check", false),
                    new("Items held", Count(_ => true), "Items this warehouse carries", StatTone.Neutral, "list-check", false)
                ];
            }

            return
            [
                new("On the shelf then", Count(row => row.Then > 0), $"Against {Figure(Count(row => row.Now > 0))} now", StatTone.Neutral, "box", true),
                new("At zero then", Count(row => row.Then <= 0), "Nothing on hand at that moment", StatTone.Neutral, "warning", false),
                new("Up since", Count(row => row.Change > 0), "Items holding more now", StatTone.Ok, "arrow-up", false),
                new("Down since", Count(row => row.Change < 0), "Items holding less now", StatTone.Neutral, "arrow-down", false)
            ];
        }
    }

    private string PresetSub(Moment preset)
    {
        var now = DateTime.UtcNow;
        return preset switch
        {
            Moment.Now => "Live from SAP",
            Moment.Opening => Cat(StockDayClock.OpeningUtc(now)),
            Moment.LastClose => "End of yesterday's stock day",
            Moment.SameTimeYesterday => Cat(now.AddDays(-1)),
            _ => string.Empty
        };
    }

    private string BannerClass => mode == Moment.Now ? "is-live" : recorded ? "is-past" : "is-missing";

    private string BannerTitle => asAtUtc is { } at
        ? $"As at {IAuditService.ToCAT(at):ddd d MMM yyyy, HH:mm}"
        : $"Live · read from SAP{(loadedAt is { } stamp ? $" at {stamp:HH:mm}" : string.Empty)}";

    private string BannerBody => mode == Moment.Now
        ? "On hand, committed and free to sell as SAP holds them now. Free to sell below zero means more is promised than is on the shelf."
        : "Rebuilt from the stock ledger: SAP's 07:00 count for that day plus every sale, return, transfer and SAP correction journalled up to the moment. Committed is not kept historically, so it shows for Now only.";

    private string NotRecordedText => recordedFrom is { } first
        ? $"There is no stock record for this warehouse at that moment. Its ledger starts on {first:ddd d MMM yyyy}; pick a moment after 07:00 that day."
        : "This warehouse has no stock ledger, so only Now can be shown. The ledger covers the warehouses the 07:00 stock count runs for: the shops and vans.";

    private string MomentShort => asAtUtc is { } at ? IAuditService.ToCAT(at).ToString("d MMM HH:mm") : "now";

    private string DetailFromLabel => asAtUtc is { } at && detailNote is null
        ? $"At {IAuditService.ToCAT(at):d MMM HH:mm}"
        : $"From {IAuditService.ToCAT(detailFromUtc):d MMM HH:mm}";

    private string DetailStartWhen => When(detailFromUtc);

    private string DetailStartLabel => asAtUtc is null ? "This morning's opening" : "On hand at the moment picked";

    private string WarehouseLabel => warehouse is null ? "Stock" : $"{NameFor(warehouse)} · {warehouse}";

    private string NameFor(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "—";
        return warehouseNames.TryGetValue(code.Trim(), out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : code.Trim();
    }

    private static string Cat(DateTime utc) => IAuditService.ToCAT(utc).ToString("ddd d MMM · HH:mm");

    private static string When(DateTime utc)
    {
        var cat = IAuditService.ToCAT(utc);
        return cat.Date == IAuditService.ToCAT(DateTime.UtcNow).Date ? cat.ToString("HH:mm") : cat.ToString("d MMM HH:mm");
    }

    private static string Figure(int? value) => value?.ToString("N0") ?? "—";

    private static string Qty(decimal value) =>
        value == Math.Round(value)
            ? value.ToString("N0", CultureInfo.InvariantCulture)
            : value.ToString("N2", CultureInfo.InvariantCulture);

    private static string Signed(decimal value) => value switch
    {
        > 0 => "+" + Qty(value),
        < 0 => "−" + Qty(-value),
        _ => "—"
    };

    private static string ChangeClass(decimal change) => change switch
    {
        > 0 => "sih-pos",
        < 0 => "sih-out",
        _ => "sih-mute"
    };
}
