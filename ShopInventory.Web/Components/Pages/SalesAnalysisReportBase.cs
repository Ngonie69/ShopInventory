using System.Globalization;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.JSInterop;
using ShopInventory.Web.Data;
using ShopInventory.Web.Features.ItemVolumeConversions.Queries.GetItemVolumeConversions;
using ShopInventory.Web.Features.Reports.Queries.GetItemVolumeSalesReport;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

/// <summary>
/// Sales analysis: one window over posted invoices and credit notes, read either as volume moved
/// or as money earned.
/// </summary>
/// <remarks>
/// <para>
/// One request backs both lenses, and switching between them does not re-ask SAP. The volume and
/// the revenue come off the same invoice and credit-note lines, so a second call would double the
/// read to answer the same question twice — and would let the two lenses disagree about a period,
/// which is the failure that matters most to a reader holding both.
/// </para>
/// <para>
/// Every figure on this page is net of credit notes. A credit note is selected on its own DocDate,
/// so one raised inside the window reduces the window even when the invoice it corrects was raised
/// before it. That is what makes these figures a statement about the period rather than about a set
/// of invoices, and it is why a heavily credited period can report a negative rather than being
/// floored at zero. The API negates credit lines on the way in — see its handler — so everything
/// here is a plain sum.
/// </para>
/// <para>
/// Returnable crates are the one thing a reader can take back out of the window without re-running
/// it. They move quantity and money like an item and are not one, and the filter is applied to the
/// loaded result, so the toggle answers immediately and the pivot, the chart and the workbook are
/// all read off the same filtered object — see <see cref="ItemVolumeSalesCrates"/>.
/// </para>
/// </remarks>
public abstract class SalesAnalysisReportBase : ComponentBase, IDisposable
{
    [Inject] protected IMediator Mediator { get; set; } = default!;
    [Inject] protected IAuditService AuditService { get; set; } = default!;
    [Inject] protected IReportExportService ExportService { get; set; } = default!;
    [Inject] protected IJSRuntime JS { get; set; } = default!;
    [Inject] protected IDbContextFactory<WebAppDbContext> DbContextFactory { get; set; } = default!;
    [Inject] protected NavigationManager Navigation { get; set; } = default!;
    [Inject] protected ILogger<SalesAnalysisReportBase> Logger { get; set; } = default!;

    /// <summary>Which face of the window is on screen.</summary>
    protected enum ReportLens
    {
        Volume,
        Revenue
    }

    /// <summary>The window presets, plus the state of having typed your own dates.</summary>
    protected enum PeriodPreset
    {
        LastFourMonths,
        QuarterToDate,
        YearToDate,
        Custom
    }

    /// <summary>
    /// One column of the pivot: a period of the window, and whether the window cuts it short.
    /// </summary>
    protected sealed record PeriodColumn(string Label, bool IsPartial, ItemVolumeSalesPeriodResult Period);

    /// <summary>One entity's figures across the columns. <c>Total</c> is the sum of the cells shown.</summary>
    protected sealed record PivotRow(
        string Code,
        string Name,
        IReadOnlyList<decimal> Cells,
        decimal Total,
        decimal SharePercent,
        bool IsConverted);

    /// <summary>A run made in this session, so it can be picked up again without retyping it.</summary>
    protected sealed record RecentRun(
        string Title,
        string Meta,
        ReportLens Lens,
        PeriodPreset Preset,
        DateTime? FromDate,
        DateTime? ToDate,
        ItemVolumeSalesGrouping Grouping,
        IReadOnlyList<string> Accounts,
        IReadOnlyList<string> Items);

    /// <summary>One coloured band of a bar, named and measured, for the hover card.</summary>
    protected sealed record ChartBand(
        string Code,
        string Name,
        decimal Value,
        decimal SharePercent,
        string Fill);

    /// <summary>
    /// Everything a reader can ask of a single bar. Built once per column per render rather than on
    /// the hover itself: the card is plain markup revealed by CSS, so reading it costs no round trip
    /// to the circuit and it opens on a keyboard focus as readily as on a pointer.
    /// </summary>
    protected sealed record ChartHover(
        string Label,
        string Span,
        bool IsPartial,
        decimal Total,
        decimal WindowSharePercent,
        int InvoiceCount,
        int CreditNoteCount,
        decimal NetQuantity,
        IReadOnlyList<ChartBand> Bands,
        decimal Other,
        int OtherCount);

    /// <summary>The window as it was loaded, before the crate filter.</summary>
    private GetItemVolumeSalesReportResult? loadedReport;

    /// <summary>
    /// The window every figure on the page is read from — <see cref="loadedReport"/> with the crates
    /// already taken out when they are being left out. One filtered result feeds the pivot, the
    /// chart and the workbook, so none of the three can disagree about what was counted.
    /// </summary>
    protected GetItemVolumeSalesReportResult? report;
    protected List<SalesAnalysisPicker.Option> accountOptions = new();
    protected List<SalesAnalysisPicker.Option> itemOptions = new();
    protected IReadOnlyList<string> selectedAccounts = new List<string>();
    protected IReadOnlyList<string> selectedItems = new List<string>();

    protected ReportLens lens = ReportLens.Volume;
    protected PeriodPreset preset = PeriodPreset.LastFourMonths;
    protected DateTime? fromDate;
    protected DateTime? toDate;
    protected ItemVolumeSalesGrouping grouping = ItemVolumeSalesGrouping.Monthly;

    /// <summary>
    /// Which currency the revenue lens reads. The two are never added together — there is no rate
    /// on the document that would make a combined figure honest — so a pivot of money has to name
    /// one of them and say so.
    /// </summary>
    protected bool readZig;

    /// <summary>
    /// Whether returnable crates count as items. On by default, so a report reads the same as it
    /// always has until somebody says otherwise — see <see cref="ItemVolumeSalesCrates"/> for why
    /// anyone would want them out.
    /// </summary>
    protected bool includeCrates = true;

    /// <summary>
    /// Whether the query is open for editing. Open before the first run — there is nothing else to
    /// look at — and closed after one, where the run itself is the answer and the query becomes a
    /// single line describing it.
    /// </summary>
    protected bool isEditingQuery = true;

    /// <summary>Narrows the pivot on screen only. The total row stays the total of every row.</summary>
    protected string tableFilter = string.Empty;

    /// <summary>Hides rows that came to nothing over the whole window.</summary>
    protected bool hideEmptyRows;

    protected bool isLoading;
    protected bool isExporting;
    protected bool isLoadingOptions = true;
    protected string? errorMessage;
    protected string? optionsWarning;
    protected string? toast;

    /// <summary>Newest first, this session only.</summary>
    protected readonly List<RecentRun> recentRuns = new();

    private DateTime ranAtUtc;

    /// <summary>The window the loaded report was actually run for, not the one in the filters.</summary>
    private DateTime reportToDate;

    private CancellationTokenSource loadCts = new();
    private bool isDisposed;
    private readonly HashSet<string> loggedViews = new(StringComparer.Ordinal);

    private const int MaxRecentRuns = 5;

    /// <summary>Entities stacked in the chart, and coloured in the legend. The rest fall into the table only.</summary>
    private const int ChartSeries = 6;

    protected bool IsVolume => lens == ReportLens.Volume;

    protected bool HasRun => report is not null;

    protected bool CanRun => selectedAccounts.Count > 0 && fromDate.HasValue && toDate.HasValue && !isLoading;

    protected bool CanExport => report is not null && !isLoading && !isExporting;

    /// <summary>Names the report in the audit log and the exported workbook.</summary>
    protected string ReportName => IsVolume ? "Item Volume" : "Customer Revenue";

    protected override async Task OnInitializedAsync()
    {
        lens = Navigation.Uri.Contains("/reports/customer-revenue", StringComparison.OrdinalIgnoreCase)
            ? ReportLens.Revenue
            : ReportLens.Volume;

        ApplyPreset(PeriodPreset.LastFourMonths);
        await LoadPickerOptionsAsync();
    }

    /// <summary>
    /// Fills both pickers from the local caches, and marks the items that carry no conversion
    /// factor so the gap shows while choosing rather than only in the result.
    /// </summary>
    private async Task LoadPickerOptionsAsync()
    {
        try
        {
            await using var db = await DbContextFactory.CreateDbContextAsync();

            var partners = await db.CachedBusinessPartners
                .AsNoTracking()
                .Where(partner => partner.IsActive)
                .OrderBy(partner => partner.CardCode)
                .Select(partner => new { partner.CardCode, partner.CardName })
                .ToListAsync();

            accountOptions = partners
                .Select(partner => new SalesAnalysisPicker.Option(
                    partner.CardCode,
                    string.IsNullOrWhiteSpace(partner.CardName) ? partner.CardCode : partner.CardName))
                .ToList();

            var products = await db.CachedProducts
                .AsNoTracking()
                .Where(product => product.IsActive)
                .OrderBy(product => product.ItemCode)
                .Select(product => new { product.ItemCode, product.ItemName })
                .ToListAsync();

            var conversionResult = await Mediator.Send(new GetItemVolumeConversionsQuery(IncludeInactive: false));
            var factorCodes = conversionResult.IsError
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : conversionResult.Value.Conversions
                    .Select(conversion => conversion.ItemCode)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (conversionResult.IsError)
            {
                optionsWarning = "The conversion factors could not be loaded, so the item picker cannot show which items convert.";
            }

            itemOptions = products
                .Select(product => new SalesAnalysisPicker.Option(
                    product.ItemCode,
                    string.IsNullOrWhiteSpace(product.ItemName) ? product.ItemCode : product.ItemName,
                    factorCodes.Contains(product.ItemCode) ? null : "no factor"))
                .ToList();

            if (accountOptions.Count == 0)
            {
                optionsWarning = "No business partners are cached yet. Type the codes you need instead.";
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load the pickers for the sales analysis report");
            optionsWarning = "The pickers could not be filled from the local cache. Type the codes you need instead.";
        }
        finally
        {
            isLoadingOptions = false;
        }
    }

    // ── The filters ──────────────────────────────────────────────────────────

    /// <summary>
    /// Switching lens keeps the loaded result: it is the same window read a different way, and
    /// re-asking SAP for it would be a second read of the same rows. The address follows so the
    /// page can still be linked to, and both routes reach this component, so the swap does not
    /// throw the result away.
    /// </summary>
    protected void PickLens(ReportLens next)
    {
        if (lens == next)
        {
            return;
        }

        lens = next;
        errorMessage = null;
        InvalidateView();

        Navigation.NavigateTo(
            next == ReportLens.Volume ? "/reports/item-volume" : "/reports/customer-revenue",
            forceLoad: false,
            replace: true);
    }

    protected void ApplyPreset(PeriodPreset next)
    {
        preset = next;
        var today = DateTime.UtcNow.Date;

        switch (next)
        {
            case PeriodPreset.LastFourMonths:
                fromDate = new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-3);
                toDate = today;
                break;
            case PeriodPreset.QuarterToDate:
                fromDate = new DateTime(today.Year, (((today.Month - 1) / 3) * 3) + 1, 1, 0, 0, 0, DateTimeKind.Utc);
                toDate = today;
                break;
            case PeriodPreset.YearToDate:
                fromDate = new DateTime(today.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                toDate = today;
                break;
        }
    }

    protected void OnFromDateChanged(DateTime? value)
    {
        fromDate = value;
        preset = PeriodPreset.Custom;
    }

    protected void OnToDateChanged(DateTime? value)
    {
        toDate = value;
        preset = PeriodPreset.Custom;
    }

    protected void SetGrouping(ItemVolumeSalesGrouping next) => grouping = next;

    protected void SetCurrency(bool zig)
    {
        readZig = zig;
        InvalidateView();
    }

    /// <summary>
    /// Puts the crates in or takes them out. It re-reads the window already in hand rather than
    /// asking SAP again — nothing about the request changes, only which of its lines are counted —
    /// so the answer is on screen before the pointer leaves the control, run or no run.
    /// </summary>
    protected void SetIncludeCrates(bool include)
    {
        if (includeCrates == include)
        {
            return;
        }

        includeCrates = include;
        ApplyCrateFilter();
    }

    protected void ToggleQueryEditor() => isEditingQuery = !isEditingQuery;

    protected void OnTableFilterChanged(ChangeEventArgs args) =>
        tableFilter = args.Value?.ToString() ?? string.Empty;

    protected void OnHideEmptyChanged(ChangeEventArgs args) =>
        hideEmptyRows = args.Value is bool value && value;

    protected void OnAccountsChanged(IReadOnlyList<string> accounts) => selectedAccounts = accounts;

    protected void OnItemsChanged(IReadOnlyList<string> items) => selectedItems = items;

    protected void ResetFilters()
    {
        ApplyPreset(PeriodPreset.LastFourMonths);
        grouping = ItemVolumeSalesGrouping.Monthly;
        readZig = false;
        includeCrates = true;
        selectedAccounts = new List<string>();
        selectedItems = new List<string>();
        loadedReport = null;
        report = null;
        errorMessage = null;
        tableFilter = string.Empty;
        hideEmptyRows = false;
        isEditingQuery = true;
        InvalidateView();
    }

    // ── Running ──────────────────────────────────────────────────────────────

    protected async Task RunReportAsync()
    {
        if (selectedAccounts.Count == 0)
        {
            errorMessage = "Choose at least one business partner.";
            return;
        }

        var cancellationToken = BeginLoad();
        isLoading = true;
        errorMessage = null;

        var requestedGrouping = grouping;
        var requestedTo = toDate;

        try
        {
            var result = await Mediator.Send(
                new GetItemVolumeSalesReportQuery(
                    fromDate,
                    toDate,
                    requestedGrouping,
                    string.Join(",", selectedAccounts),
                    string.Join(",", selectedItems)),
                cancellationToken);

            if (cancellationToken.IsCancellationRequested || isDisposed)
            {
                return;
            }

            result.SwitchFirst(
                value =>
                {
                    loadedReport = value;
                    ranAtUtc = DateTime.UtcNow;
                    reportToDate = requestedTo?.Date ?? value.ToDateUtc.Date;
                    isEditingQuery = false;
                    ApplyCrateFilter();
                    RememberRun();
                },
                error =>
                {
                    loadedReport = null;
                    report = null;
                    errorMessage = error.Description;
                    InvalidateView();
                });

            // Once per lens per circuit. Both lenses are the same page now, so logging once
            // would credit every run of the session to whichever one was opened first.
            if (!result.IsError && loggedViews.Add(ReportName))
            {
                await AuditService.LogAsync(AuditActions.ViewReports, "Report", ReportName);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load {ReportName}", ReportName);
            report = null;
            errorMessage = "Failed to load the report.";
        }
        finally
        {
            if (!isDisposed)
            {
                isLoading = false;
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    private void RememberRun()
    {
        var run = new RecentRun(
            $"{(IsVolume ? "Item volume" : "Customer revenue")} · {PartnerSentence}",
            $"{IAuditService.ToCAT(ranAtUtc):dd MMM HH:mm} · {GroupLabel.ToLowerInvariant()}",
            lens,
            preset,
            fromDate,
            toDate,
            grouping,
            selectedAccounts.ToList(),
            selectedItems.ToList());

        recentRuns.Insert(0, run);

        if (recentRuns.Count > MaxRecentRuns)
        {
            recentRuns.RemoveRange(MaxRecentRuns, recentRuns.Count - MaxRecentRuns);
        }
    }

    /// <summary>Puts a run's filters back and asks SAP again — the figures may have moved since.</summary>
    protected async Task LoadRecentRunAsync(RecentRun run)
    {
        lens = run.Lens;
        preset = run.Preset;
        fromDate = run.FromDate;
        toDate = run.ToDate;
        grouping = run.Grouping;
        selectedAccounts = run.Accounts.ToList();
        selectedItems = run.Items.ToList();

        await RunReportAsync();
    }

    protected async Task ExportToExcelAsync()
    {
        if (report is null)
        {
            return;
        }

        isExporting = true;
        errorMessage = null;

        try
        {
            // The result handed over is already crate-filtered, so the workbook agrees with the
            // screen — but a workbook outlives the page it came off, so the title has to say so.
            var bytes = ExportService.ExportItemVolumeSalesReportToExcel(
                report,
                includeCrates ? ReportName : $"{ReportName} (crates excluded)");
            var base64 = Convert.ToBase64String(bytes);
            await JS.InvokeVoidAsync(
                "downloadFile",
                $"{ReportName.Replace(' ', '_')}_{IAuditService.ToCAT(DateTime.UtcNow):yyyyMMdd_HHmm}.xlsx",
                base64);

            toast = $"Workbook prepared — {Rows.Count} rows, {Columns.Count} {(Columns.Count == 1 ? "period" : "periods")}.";
            _ = DismissToastLaterAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to export {ReportName} to Excel", ReportName);
            errorMessage = "Failed to export the report to Excel.";
        }
        finally
        {
            isExporting = false;
        }
    }

    protected void DismissToast() => toast = null;

    /// <summary>
    /// The toast clears itself as well as on a click. Not awaited by the export, which has
    /// finished by then; the disposal check is what keeps it off a torn-down circuit.
    /// </summary>
    private async Task DismissToastLaterAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(4));

        if (isDisposed)
        {
            return;
        }

        toast = null;
        await InvokeAsync(StateHasChanged);
    }

    // ── The pivot ────────────────────────────────────────────────────────────

    private List<PeriodColumn>? cachedColumns;
    private List<PivotRow>? cachedRows;
    private List<decimal>? cachedColumnTotals;

    /// <summary>
    /// Throws away the pivot so the next read rebuilds it. Called whenever the lens, the currency
    /// or the result itself changes — building it is a walk over every period × account × item, so
    /// it is not something to do once per property read during a render.
    /// </summary>
    private void InvalidateView()
    {
        cachedColumns = null;
        cachedRows = null;
        cachedColumnTotals = null;
    }

    /// <summary>
    /// Re-reads the loaded window under the current crate setting. Cheap enough to run on a click:
    /// it walks the result once and returns it untouched when no crate moved.
    /// </summary>
    private void ApplyCrateFilter()
    {
        report = loadedReport is null || includeCrates
            ? loadedReport
            : ItemVolumeSalesCrates.Exclude(loadedReport);

        InvalidateView();
    }

    /// <summary>Crate codes that actually moved in the loaded window, whether or not they are counted.</summary>
    protected List<string> CratesInWindow => loadedReport is null
        ? new List<string>()
        : ItemVolumeSalesCrates.CratesIn(loadedReport);

    /// <summary>
    /// The rows on screen. The filter and the hide-empty switch narrow what is shown and nothing
    /// else — every total on the page stays the total of the whole window, because a figure that
    /// quietly followed a search box would be a different report each time somebody typed.
    /// </summary>
    protected List<PivotRow> VisibleRows
    {
        get
        {
            var query = Rows.AsEnumerable();

            if (hideEmptyRows)
            {
                query = query.Where(row => row.Total != 0);
            }

            var needle = tableFilter.Trim();

            if (needle.Length > 0)
            {
                query = query.Where(row =>
                    row.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                    row.Code.Contains(needle, StringComparison.OrdinalIgnoreCase));
            }

            return query.ToList();
        }
    }

    protected bool IsTableNarrowed => hideEmptyRows || tableFilter.Trim().Length > 0;

    /// <summary>
    /// The period columns. Every period of the window is drawn, including the ones nothing moved
    /// in — a quiet month is a finding, and dropping it would make the row stop adding up.
    /// </summary>
    protected List<PeriodColumn> Columns => cachedColumns ??= report is null
        ? new List<PeriodColumn>()
        : report.Periods
            .Select(period => new PeriodColumn(
                ShortPeriodLabel(period),
                period.PeriodEndUtc.Date > reportToDate,
                period))
            .ToList();

    /// <summary>
    /// One row per item on the volume lens, one per business partner on the revenue lens, ordered
    /// by what they came to over the whole window.
    /// </summary>
    /// <remarks>
    /// An item with no conversion factor still gets a row. Its quantity is real; only its volume is
    /// unknown, and a table that silently dropped it would be short with nothing on screen to
    /// explain why. Those rows carry no figures and sort last.
    /// </remarks>
    protected List<PivotRow> Rows => cachedRows ??= BuildRows();

    private List<PivotRow> BuildRows()
    {
        if (report is null)
        {
            return new List<PivotRow>();
        }

        var columns = Columns;

        // One walk over the periods each way, keyed by code, rather than a scan per row per
        // column: a year of a hundred partners is otherwise a million comparisons a render.
        var rows = IsVolume
            ? BuildVolumeRows(columns)
            : BuildRevenueRows(columns);

        var grand = rows.Sum(row => Math.Abs(row.Total));

        return rows
            .Select(row => row with { SharePercent = grand == 0 ? 0m : Math.Abs(row.Total) / grand * 100m })
            .OrderByDescending(row => row.IsConverted)
            .ThenByDescending(row => row.Total)
            .ThenBy(row => row.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<PivotRow> BuildVolumeRows(List<PeriodColumn> columns)
    {
        var cells = new Dictionary<string, decimal[]>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < columns.Count; index++)
        {
            foreach (var item in columns[index].Period.Accounts.SelectMany(account => account.Items))
            {
                if (!cells.TryGetValue(item.ItemCode, out var row))
                {
                    row = new decimal[columns.Count];
                    cells[item.ItemCode] = row;
                }

                row[index] += item.NetVolume;
            }
        }

        return report!.ItemTotals
            .Select(item => BuildRow(
                item.ItemCode,
                item.ItemName,
                item.HasVolumeFactor && cells.TryGetValue(item.ItemCode, out var row)
                    ? row
                    : new decimal[columns.Count],
                item.HasVolumeFactor))
            .ToList();
    }

    private List<PivotRow> BuildRevenueRows(List<PeriodColumn> columns)
    {
        var cells = new Dictionary<string, decimal[]>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < columns.Count; index++)
        {
            foreach (var account in columns[index].Period.Accounts)
            {
                if (!cells.TryGetValue(account.CardCode, out var row))
                {
                    row = new decimal[columns.Count];
                    cells[account.CardCode] = row;
                }

                row[index] += readZig ? account.NetRevenueZig : account.NetRevenueUsd;
            }
        }

        return report!.AccountTotals
            .Where(account => account.InvoiceCount > 0 || account.CreditNoteCount > 0)
            .Select(account => BuildRow(
                account.CardCode,
                account.CardName,
                cells.TryGetValue(account.CardCode, out var row) ? row : new decimal[columns.Count],
                isConverted: true))
            .ToList();
    }

    private static PivotRow BuildRow(string code, string name, IReadOnlyList<decimal> cells, bool isConverted) =>
        new(code, name, cells, cells.Sum(), 0m, isConverted);

    /// <summary>Column totals, in the same order as <see cref="Columns"/>.</summary>
    protected List<decimal> ColumnTotals => cachedColumnTotals ??= Columns
        .Select((_, index) => Rows.Sum(row => row.Cells[index]))
        .ToList();

    protected decimal GrandTotal => Rows.Sum(row => row.Total);

    /// <summary>
    /// The rows the chart stacks. Only the largest few are coloured; beyond that the bands stop
    /// being distinguishable and the legend stops being readable.
    /// </summary>
    protected List<PivotRow> ChartRows => Rows.Where(row => row.IsConverted).Take(ChartSeries).ToList();

    /// <summary>The tallest column, which the bars are drawn against. Never zero, so nothing divides by it.</summary>
    protected decimal ChartScale
    {
        get
        {
            var totals = ColumnTotals.Where(total => total > 0).ToList();
            return totals.Count == 0 ? 1m : totals.Max();
        }
    }

    /// <summary>
    /// A bar's height as a percentage of the tallest column. A period that credited back more than
    /// it invoiced has no bar to draw — the table carries the negative, which is where a reader
    /// can read it against the other columns.
    /// </summary>
    protected decimal BarPercent(decimal value) =>
        value <= 0 ? 0m : Math.Min(100m, value / ChartScale * 100m);

    protected decimal SegmentPercent(decimal cell, decimal columnTotal) =>
        columnTotal <= 0 || cell <= 0 ? 0m : Math.Min(100m, cell / columnTotal * 100m);

    /// <summary>
    /// Everything behind one bar, for the card that opens over it. A stacked bar can only ever show
    /// proportions, so what a reader actually wants from it — what the period came to, what each
    /// band is worth, how much of it is the tail the colours do not name, and how many documents
    /// made it — has to be written out somewhere. This is that somewhere.
    /// </summary>
    protected ChartHover HoverFor(int index)
    {
        var columns = Columns;
        var column = columns[index];
        var total = ColumnTotals[index];
        var chartRows = ChartRows;

        var bands = chartRows
            .Select((row, series) => new ChartBand(
                row.Code,
                row.Name,
                row.Cells[index],
                total == 0 ? 0m : row.Cells[index] / total * 100m,
                SeriesFill(series)))
            .Where(band => band.Value != 0)
            .ToList();

        var named = chartRows.Sum(row => row.Cells[index]);

        // The tail the palette stopped naming: every row past the sixth, plus any row the chart
        // never draws because it cannot be converted to volume.
        var otherCount = Rows.Count(row => row.Cells[index] != 0) - bands.Count;

        return new ChartHover(
            column.Label,
            SpanOf(column),
            column.IsPartial,
            total,
            GrandTotal == 0 ? 0m : total / GrandTotal * 100m,
            column.Period.InvoiceCount,
            column.Period.CreditNoteCount,
            column.Period.NetQuantity,
            bands,
            total - named,
            Math.Max(0, otherCount));
    }

    /// <summary>
    /// The dates a column actually covers, clipped to the window. A month column at either end of
    /// the range is only part of a month, and the label alone does not say which part.
    /// </summary>
    private string SpanOf(PeriodColumn column)
    {
        if (report is null)
        {
            return string.Empty;
        }

        var from = column.Period.PeriodStartUtc.Date < report.FromDateUtc.Date
            ? report.FromDateUtc.Date
            : column.Period.PeriodStartUtc.Date;

        var to = column.Period.PeriodEndUtc.Date > reportToDate
            ? reportToDate
            : column.Period.PeriodEndUtc.Date;

        return from.Year == to.Year
            ? $"{from:dd MMM} – {to:dd MMM yyyy}"
            : $"{from:dd MMM yyyy} – {to:dd MMM yyyy}";
    }

    /// <summary>
    /// The stack's fills, from the Nocturne accent ramp. Six, because that is how many bands a
    /// 64px-wide bar can hold and still be read.
    /// </summary>
    protected static string SeriesFill(int index) => index switch
    {
        0 => "var(--sla-series-1)",
        1 => "var(--sla-series-2)",
        2 => "var(--sla-series-3)",
        3 => "var(--sla-series-4)",
        4 => "var(--sla-series-5)",
        _ => "var(--sla-series-6)"
    };

    // The chart's geometry is data, so it has to be written as inline style. It is built here
    // rather than in the markup because a percentage has to be formatted invariantly — a comma
    // decimal separator is not a length, and the bar would silently collapse.

    protected string BarStyle(decimal columnTotal) => $"height:{Percent(BarPercent(columnTotal))}";

    /// <summary>
    /// Sits the hover card just above the bar it belongs to rather than at a fixed height, so the
    /// card and the thing it describes are always next to each other. The bar's height is a
    /// percentage of the plot, which is why this is the one bit of the card's geometry that is data.
    /// </summary>
    protected string HoverStyle(decimal columnTotal) =>
        $"bottom:calc({Percent(BarPercent(columnTotal))} + 12px)";

    protected string SegmentStyle(decimal cell, decimal columnTotal, int series) =>
        $"height:{Percent(SegmentPercent(cell, columnTotal))};background:{SeriesFill(series)}";

    protected static string LegendStyle(int series) => $"background:{SeriesFill(series)}";

    /// <summary>A share bar keeps a 2% stub so a real but tiny share is still visible.</summary>
    protected static string ShareStyle(decimal percent) => $"width:{Percent(Math.Max(2m, percent))}";

    protected static string FormatShare(decimal percent) =>
        percent.ToString("0.0", CultureInfo.InvariantCulture) + "%";

    private static string Percent(decimal value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture) + "%";

    protected int StrongestColumnIndex
    {
        get
        {
            var totals = ColumnTotals;
            if (totals.Count == 0)
            {
                return -1;
            }

            var best = 0;
            for (var index = 1; index < totals.Count; index++)
            {
                if (totals[index] > totals[best])
                {
                    best = index;
                }
            }

            return best;
        }
    }

    protected bool HasPartialColumn => Columns.Any(column => column.IsPartial);

    // ── Words and numbers ────────────────────────────────────────────────────

    /// <summary>
    /// A column heading. The API's own label is a sentence — "August 2026", "01 Jan 2026 - 05 Aug
    /// 2026" — which is right in the workbook and far too wide for a column, so the heading is cut
    /// from the period's dates instead.
    /// </summary>
    /// <remarks>
    /// Cut against the grouping the <em>result</em> was run with, not the one in the filter bar.
    /// Changing "Group by" does not re-run anything, so a heading taken from the filter would
    /// relabel monthly columns as quarters the moment anything else rebuilt the pivot.
    /// </remarks>
    private string ShortPeriodLabel(ItemVolumeSalesPeriodResult period) => ReportGrouping switch
    {
        ItemVolumeSalesGrouping.Total => "Period total",
        ItemVolumeSalesGrouping.Quarterly => string.Create(
            CultureInfo.InvariantCulture,
            $"Q{((period.PeriodStartUtc.Month - 1) / 3) + 1} {period.PeriodStartUtc:yyyy}"),
        _ => SpansYears
            ? period.PeriodStartUtc.ToString("MMM yy", CultureInfo.InvariantCulture)
            : period.PeriodStartUtc.ToString("MMM", CultureInfo.InvariantCulture)
    };

    private bool SpansYears => report is not null && report.FromDateUtc.Year != report.ToDateUtc.Year;

    protected string EntityLabel => IsVolume ? "Item" : "Business partner";

    protected string EntityPlural => IsVolume ? "items" : "partners";

    protected string Currency => readZig ? "ZiG" : "USD";

    protected string UnitNote => IsVolume
        ? "litres, invoiced less credited"
        : $"{Currency}, net of credit notes";

    private ItemVolumeSalesGrouping ReportGrouping => report?.Grouping ?? grouping;

    protected string GroupLabel => Name(grouping);

    /// <summary>
    /// The tags over the result describe the result, so they are read off it rather than off the
    /// filter bar — the two disagree the moment a filter is changed without running again.
    /// </summary>
    protected string ReportGroupLabel => Name(ReportGrouping);

    private static string Name(ItemVolumeSalesGrouping value) => value switch
    {
        ItemVolumeSalesGrouping.Quarterly => "Quarterly",
        ItemVolumeSalesGrouping.Total => "Period total",
        ItemVolumeSalesGrouping.Weekly => "Weekly",
        ItemVolumeSalesGrouping.Daily => "Daily",
        _ => "Monthly"
    };

    protected string PeriodSentence => fromDate.HasValue && toDate.HasValue
        ? $"{fromDate.Value:dd MMM yyyy} – {toDate.Value:dd MMM yyyy}"
        : "no range";

    protected string ReportPeriodSentence => report is null
        ? PeriodSentence
        : $"{report.FromDateUtc:dd MMM yyyy} – {report.ToDateUtc:dd MMM yyyy}";

    protected string ReportPartnerSentence => report?.RequestedAccountCodes.Count switch
    {
        null => PartnerSentence,
        1 => "1 partner",
        var count => $"{count} partners"
    };

    protected string ReportItemSentence => report?.RequestedItemCodes.Count switch
    {
        null => ItemSentence,
        0 => "all items traded",
        1 => "1 item",
        var count => $"{count} items"
    };

    protected string PartnerSentence => selectedAccounts.Count switch
    {
        0 => "no partners",
        1 => "1 partner",
        var count => $"{count} partners"
    };

    protected string ItemSentence => selectedItems.Count switch
    {
        0 => "all items traded",
        1 => "1 item",
        var count => $"{count} items"
    };

    protected string ReadySentence => $"{GroupLabel} · {PeriodSentence} · {PartnerSentence} · {ItemSentence}";

    /// <summary>
    /// The one line the closed query bar carries. Read off the result rather than the filter bar, so
    /// it describes the figures underneath it and not whatever has been typed since.
    /// </summary>
    protected string SummaryLine
    {
        get
        {
            var parts = new List<string>
            {
                ReportGroupLabel,
                ReportPeriodSentence,
                ReportPartnerSentence,
                ReportItemSentence
            };

            if (!IsVolume)
            {
                parts.Add(Currency);
            }

            if (!includeCrates)
            {
                parts.Add("crates excluded");
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>How many crate codes the toggle is currently keeping out of the figures.</summary>
    protected string CrateSentence => CratesInWindow.Count switch
    {
        0 => "no crates moved in this window",
        1 => "1 crate code moved in this window",
        var count => $"{count} crate codes moved in this window"
    };

    protected string RanAt => ranAtUtc == default
        ? string.Empty
        : IAuditService.ToCAT(ranAtUtc).ToString("dd MMM yyyy HH:mm 'CAT'", CultureInfo.InvariantCulture);

    /// <summary>A pivot cell. Nothing at all reads better as a dash than as a run of zeros.</summary>
    protected string FormatCell(decimal value, bool isConverted)
    {
        if (!isConverted)
        {
            return "—";
        }

        return value == 0 ? "—" : FormatValue(value);
    }

    /// <summary>
    /// Both lenses read to two decimals. Volume once carried three, and across a pivot of
    /// mostly-whole litres that third place was a column of noise on every figure. The report
    /// itself now rounds to two, so this format is not hiding a longer number.
    /// </summary>
    protected static string FormatValue(decimal value) =>
        value.ToString("N2", CultureInfo.InvariantCulture);

    /// <summary>The headline figure, carrying its unit — the only place the unit is spelled out.</summary>
    protected string FormatHeadline(decimal value) => IsVolume
        ? $"{FormatValue(value)} L"
        : $"{Currency} {FormatValue(value)}";

    /// <summary>
    /// The unit either side of a headline figure, so the figure can be set at its own size and the
    /// unit kept quiet beside it. Volume trails its unit, money leads with it.
    /// </summary>
    protected string HeadlineLead => IsVolume ? string.Empty : Currency;

    protected string HeadlineTrail => IsVolume ? "L" : string.Empty;

    /// <summary>Units, not litres — what physically moved, whether or not it converts.</summary>
    protected static string FormatQuantity(decimal value) =>
        value.ToString("N2", CultureInfo.InvariantCulture);

    protected decimal CreditedBack => report is null
        ? 0m
        : IsVolume
            ? report.Summary.CreditedVolume
            : readZig
                ? report.Summary.CreditedSalesZig
                : report.Summary.CreditedSalesUsd;

    protected int CreditNoteCount => report?.Summary.CreditNoteCount ?? 0;

    /// <summary>
    /// True when the window traded in the currency the revenue lens is not showing. Two currencies
    /// are never added together — no rate on the document would make that honest — so the other
    /// one has to be pointed at rather than quietly folded in.
    /// </summary>
    protected bool HasOtherCurrencyActivity => report is not null &&
        !IsVolume &&
        (readZig
            ? report.Summary.InvoicedSalesUsd != 0 || report.Summary.CreditedSalesUsd != 0
            : report.Summary.InvoicedSalesZig != 0 || report.Summary.CreditedSalesZig != 0);

    protected string OtherCurrency => readZig ? "USD" : "ZiG";

    protected static string FormatOther(decimal value) => value.ToString("N2", CultureInfo.InvariantCulture);

    protected decimal OtherCurrencyNet => report is null
        ? 0m
        : readZig
            ? report.Summary.NetRevenueUsd
            : report.Summary.NetRevenueZig;

    private CancellationToken BeginLoad()
    {
        loadCts.Cancel();
        loadCts.Dispose();
        loadCts = new CancellationTokenSource();
        return loadCts.Token;
    }

    public void Dispose()
    {
        isDisposed = true;
        loadCts.Cancel();
        loadCts.Dispose();
        GC.SuppressFinalize(this);
    }
}
