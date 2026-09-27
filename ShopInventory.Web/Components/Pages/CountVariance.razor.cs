using System.Globalization;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MudBlazor;
using ShopInventory.Web.Features.CountVariance.Queries.GetCountingDocuments;
using ShopInventory.Web.Features.CountVariance.Queries.GetCountVariance;
using ShopInventory.Web.Features.CountVariance.Queries.GetVanCountVariance;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

/// <summary>
/// An SAP inventory count's variance, valued at selling price.
/// </summary>
/// <remarks>
/// <para>
/// The price is the van sales price list's, before VAT, so a short reads as the takings it cost rather
/// than the stock's cost. The API decides that; this page only shows which list it used.
/// </para>
/// <para>
/// Two modes. <b>One count</b> reads a single SAP counting document. <b>All vans</b> takes every van's
/// latest count in a date range and adds them up — per van, per item and for the fleet — and a van's
/// row opens its count in the first mode, so the fleet view is where a question starts and the single
/// count is where it is answered.
/// </para>
/// <para>
/// Lines the list has no price for, and lines nobody has counted yet, are kept in the table and out
/// of the money, each under its own filter, so neither can pass for a clean line.
/// </para>
/// </remarks>
public partial class CountVariance : IDisposable
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private IReportExportService ReportExport { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private ILogger<CountVariance> Logger { get; set; } = default!;

    private readonly CancellationTokenSource _disposal = new();

    private List<CountingDocumentSummary> documents = [];
    private IReadOnlyList<NocturnePickerOption> documentOptions = [];
    private string statusFilter = "open";
    private string? findText;
    private string? selectedPickerValue;
    private Dictionary<string, int> entryByPickerValue = [];

    private CountVarianceReport? report;

    // All vans.
    private bool fleetMode;
    private DateTime? fleetFrom = TodayCat().AddDays(-6);
    private DateTime? fleetTo = TodayCat();
    private VanCountVarianceReport? fleetReport;
    private string fleetTab = FleetTabs.Vans;
    private bool isRunningFleet;
    private string? fleetError;

    private static class FleetTabs
    {
        public const string Vans = "vans";
        public const string Items = "items";
        public const string NotCounted = "notcounted";
    }
    private string lineFilter = LineFilters.All;
    private bool sortByValue = true;

    private bool isLoadingDocuments = true;
    private bool isRunning;
    private string? documentsError;
    private string? reportError;

    private static readonly NocturneSelectOption<string>[] StatusOptions =
    [
        new("open", "Open counts", "warn"),
        new("closed", "Closed counts", "neutral"),
        new("all", "All counts", "neutral") { IsUnset = true }
    ];

    private static class LineFilters
    {
        public const string All = "all";
        public const string NoPrice = "noprice";
    }

    protected override async Task OnInitializedAsync() => await LoadDocumentsAsync();

    private async Task LoadDocumentsAsync()
    {
        isLoadingDocuments = true;
        documentsError = null;

        var result = await Mediator.Send(
            new GetCountingDocumentsQuery(statusFilter, findText), _disposal.Token);

        if (result.IsError)
        {
            documents = [];
            documentsError = result.FirstError.Description;
        }
        else
        {
            documents = result.Value;
        }

        // The picker draws its value as the row's code, so the value is the number B1 shows on the count
        // rather than its internal entry. Numbers repeat only across numbering series; where two rows
        // share one, the entry is added to tell them apart.
        var repeated = documents
            .GroupBy(document => document.DocumentNumber)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet();

        entryByPickerValue = documents.ToDictionary(
            document => repeated.Contains(document.DocumentNumber)
                ? $"{document.DocumentNumber} ({document.DocumentEntry})"
                : document.DocumentNumber.ToString(CultureInfo.InvariantCulture),
            document => document.DocumentEntry);

        documentOptions = documents
            .Select(document => new NocturnePickerOption(
                entryByPickerValue.First(pair => pair.Value == document.DocumentEntry).Key,
                DocumentLabel(document),
                DocumentHint(document)))
            .ToList();

        if (selectedPickerValue is not null && !entryByPickerValue.ContainsKey(selectedPickerValue))
        {
            selectedPickerValue = null;
        }

        isLoadingDocuments = false;
    }

    private async Task OnFindKeyDown(KeyboardEventArgs args)
    {
        if (args.Key == "Enter")
        {
            await LoadDocumentsAsync();
        }
    }

    private async Task ClearFind()
    {
        findText = null;
        await LoadDocumentsAsync();
    }

    private async Task RunReportAsync()
    {
        if (selectedPickerValue is null || !entryByPickerValue.TryGetValue(selectedPickerValue, out var entry))
        {
            return;
        }

        isRunning = true;
        reportError = null;

        var result = await Mediator.Send(new GetCountVarianceQuery(entry), _disposal.Token);

        if (result.IsError)
        {
            reportError = result.FirstError.Description;
        }
        else
        {
            report = result.Value;
            lineFilter = LineFilters.All;
        }

        isRunning = false;
    }

    private async Task ExportAsync()
    {
        if (report is null)
        {
            return;
        }

        try
        {
            var bytes = ReportExport.ExportCountVarianceToExcel(report);
            var fileName = $"Count_Variance_{report.Document.DocumentNumber}_{DateTime.UtcNow:yyyyMMdd_HHmm}.xlsx";
            await JS.InvokeVoidAsync("downloadFile", fileName, Convert.ToBase64String(bytes));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogError(ex, "Exporting the variance of count {DocumentNumber} failed", report.Document.DocumentNumber);
            Snackbar.Add("The workbook could not be built.", Severity.Error);
        }
    }

    // ── All vans ────────────────────────────────────────────────────────────

    private void SetMode(bool fleet) => fleetMode = fleet;

    private void SetFleetTab(string tab) => fleetTab = tab;

    private async Task RunFleetAsync()
    {
        if (fleetFrom is not { } from || fleetTo is not { } to)
        {
            fleetError = "Choose both dates.";
            return;
        }

        isRunningFleet = true;
        fleetError = null;

        var result = await Mediator.Send(new GetVanCountVarianceQuery(from, to), _disposal.Token);

        if (result.IsError)
        {
            fleetError = result.FirstError.Description;
        }
        else
        {
            fleetReport = result.Value;
            fleetTab = FleetTabs.Vans;
        }

        isRunningFleet = false;
    }

    /// <summary>
    /// A van's row opens its count in the single-count mode, found in SAP by number so the picker
    /// shows it and "Run again" works from there.
    /// </summary>
    private async Task OpenVanCountAsync(VanCountVariance van)
    {
        fleetMode = false;
        statusFilter = "all";
        findText = van.Document.DocumentNumber.ToString(CultureInfo.InvariantCulture);
        await LoadDocumentsAsync();

        selectedPickerValue = entryByPickerValue
            .Where(pair => pair.Value == van.Document.DocumentEntry)
            .Select(pair => pair.Key)
            .FirstOrDefault();

        if (selectedPickerValue is not null)
        {
            await RunReportAsync();
        }
        else
        {
            reportError = $"Count {van.Document.DocumentNumber} could not be found in SAP again.";
        }
    }

    private async Task ExportFleetAsync()
    {
        if (fleetReport is null)
        {
            return;
        }

        try
        {
            var bytes = ReportExport.ExportVanCountVarianceToExcel(fleetReport);
            var fileName = $"Van_Count_Variance_{fleetReport.FromDate:yyyyMMdd}_{fleetReport.ToDate:yyyyMMdd}.xlsx";
            await JS.InvokeVoidAsync("downloadFile", fileName, Convert.ToBase64String(bytes));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogError(ex, "Exporting the van count variance failed");
            Snackbar.Add("The workbook could not be built.", Severity.Error);
        }
    }

    private static DateTime TodayCat() => IAuditService.ToCAT(DateTime.UtcNow).Date;

    private static string FormatDay(DateTime date) => date.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);

    private string FleetShare
    {
        get
        {
            if (fleetReport is null || fleetReport.Totals.StockValue == 0m)
                return "—";
            return Math.Abs(fleetReport.Totals.NetValue / fleetReport.Totals.StockValue)
                .ToString("0.0%", CultureInfo.InvariantCulture);
        }
    }

    // ── What the table shows ────────────────────────────────────────────────

    private IEnumerable<CountVarianceLine> VisibleLines
    {
        get
        {
            if (report is null)
            {
                return [];
            }

            var lines = report.Lines.Where(line => lineFilter switch
            {
                LineFilters.All => true,
                LineFilters.NoPrice => line.SellingPrice is null,
                _ => line.Status == lineFilter
            });

            return sortByValue
                ? lines
                    .OrderBy(line => StatusRank(line.Status))
                    .ThenBy(line => line.Status == CountVarianceLineStatus.Over
                        ? -(line.VarianceValue ?? 0m)
                        : line.VarianceValue ?? 0m)
                    .ThenBy(line => line.SellingPrice is null)
                    .ThenBy(line => line.RowNumber)
                : lines.OrderBy(line => line.RowNumber);
        }
    }

    private static int StatusRank(string status) => status switch
    {
        CountVarianceLineStatus.Short => 0,
        CountVarianceLineStatus.Over => 1,
        CountVarianceLineStatus.NotCounted => 2,
        _ => 3
    };

    private IEnumerable<(string Key, string Label, int Count)> LineFilterChips
    {
        get
        {
            var totals = report!.Totals;
            yield return (LineFilters.All, "All lines", totals.LineCount);
            yield return (CountVarianceLineStatus.Short, "Short", totals.ShortLines);
            yield return (CountVarianceLineStatus.Over, "Over", totals.OverLines);
            yield return (CountVarianceLineStatus.Matched, "Matched", totals.MatchedLines);
            if (totals.NotCountedLines > 0)
                yield return (CountVarianceLineStatus.NotCounted, "Not counted", totals.NotCountedLines);
            yield return (LineFilters.NoPrice, "No price", totals.UnpricedLines);
        }
    }

    private void SetLineFilter(string key) => lineFilter = key;

    private void SetSort(bool byValue) => sortByValue = byValue;

    /// <summary>Whether the report on screen is for the count now chosen, so the button can say "Run again".</summary>
    private bool ReportIsForSelection
        => report is not null
           && selectedPickerValue is not null
           && entryByPickerValue.TryGetValue(selectedPickerValue, out var entry)
           && entry == report.Document.DocumentEntry;

    // ── Formatting ──────────────────────────────────────────────────────────

    private static string DocumentLabel(CountingDocumentSummary document)
    {
        var date = document.CountDate is { } countDate
            ? countDate.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)
            : "no date";
        return $"{date} {document.CountTime}".TrimEnd();
    }

    private static string? DocumentHint(CountingDocumentSummary document)
    {
        var parts = new[] { document.Remarks, document.CounterName, document.Status }
            .Where(part => !string.IsNullOrWhiteSpace(part));
        return string.Join(" · ", parts);
    }

    private string Money(decimal value, bool signed = false)
    {
        var amount = Math.Abs(value).ToString("#,##0.00", CultureInfo.InvariantCulture);
        var code = fleetMode ? fleetReport?.Currency : report?.Currency;
        var currency = string.IsNullOrWhiteSpace(code) ? string.Empty : code + " ";
        if (!signed || value == 0m)
            return currency + amount;
        return (value < 0 ? "−" : "+") + currency + amount;
    }

    private static string Quantity(decimal value, bool signed = false)
    {
        var amount = Math.Abs(value).ToString("#,##0.####", CultureInfo.InvariantCulture);
        if (!signed || value == 0m)
            return amount;
        return (value < 0 ? "−" : "+") + amount;
    }

    private static string Tone(decimal value) => value < 0 ? "is-bad" : value > 0 ? "is-good" : "is-flat";

    private static string Plural(int count, string noun) => $"{count} {noun}{(count == 1 ? string.Empty : "s")}";

    private static string Plural(decimal count, string noun)
        => $"{Quantity(count)} {noun}{(count == 1m ? string.Empty : "s")}";

    private string NetShare
    {
        get
        {
            if (report is null || report.Totals.StockValue == 0m)
                return "—";
            var share = Math.Abs(report.Totals.NetValue / report.Totals.StockValue);
            return share.ToString("0.0%", CultureInfo.InvariantCulture);
        }
    }

    private static string FormatCountDate(CountingDocumentSummary document)
        => document.CountDate is { } date
            ? $"{date.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)} {document.CountTime}".TrimEnd()
            : "no count date";

    private static string FormatStamp(DateTime utc) => IAuditService.ToCAT(
            utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc))
        .ToString("HH:mm", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        _disposal.Cancel();
        _disposal.Dispose();
    }
}
