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
/// Everything the item volume report and the customer revenue report share: the
/// same filters, the same call, and the same result read two different ways.
/// </summary>
/// <remarks>
/// One request backs both pages. The volume figure and the revenue figure come
/// off the same invoice and credit-note lines, so splitting them into two
/// endpoints would double the SAP read to answer the same question twice — and
/// would let the two reports disagree about a period, which is the failure that
/// matters most to a reader holding both.
/// </remarks>
public abstract class ItemVolumeReportPageBase : ComponentBase, IDisposable
{
    [Inject] protected IMediator Mediator { get; set; } = default!;
    [Inject] protected IAuditService AuditService { get; set; } = default!;
    [Inject] protected IReportExportService ExportService { get; set; } = default!;
    [Inject] protected IJSRuntime JS { get; set; } = default!;
    [Inject] protected IDbContextFactory<WebAppDbContext> DbContextFactory { get; set; } = default!;
    [Inject] protected ILogger<ItemVolumeReportPageBase> Logger { get; set; } = default!;

    protected GetItemVolumeSalesReportResult? report;
    protected List<IvxMultiSelect.Option> accountOptions = new();
    protected List<IvxMultiSelect.Option> itemOptions = new();
    protected IReadOnlyList<string> selectedAccounts = new List<string>();
    protected IReadOnlyList<string> selectedItems = new List<string>();

    protected DateTime? fromDate = DateTime.UtcNow.Date.AddDays(-30);
    protected DateTime? toDate = DateTime.UtcNow.Date;
    protected ItemVolumeSalesGrouping grouping = ItemVolumeSalesGrouping.Monthly;

    protected bool isLoading;
    protected bool isExporting;
    protected bool isLoadingOptions = true;
    protected string? errorMessage;
    protected string? optionsWarning;

    private CancellationTokenSource loadCts = new();
    private bool isDisposed;
    private bool hasLoggedView;

    /// <summary>Names the report in the audit log and the exported workbook.</summary>
    protected abstract string ReportName { get; }

    protected bool HasRun => report is not null;

    protected bool CanRun => selectedAccounts.Count > 0 && fromDate.HasValue && toDate.HasValue && !isLoading;

    protected bool CanExport => report is not null && !isLoading && !isExporting;

    protected string PrimaryActionLabel => HasRun ? "Refresh" : "Run report";

    protected override async Task OnInitializedAsync() => await LoadPickerOptionsAsync();

    /// <summary>
    /// Fills both pickers from the local caches, and marks the items that have no
    /// conversion factor so the gap is visible while choosing rather than only in
    /// the result.
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
                .Select(partner => new IvxMultiSelect.Option(partner.CardCode, partner.CardCode, partner.CardName))
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
                .Select(product => new IvxMultiSelect.Option(
                    product.ItemCode,
                    product.ItemCode,
                    product.ItemName,
                    factorCodes.Contains(product.ItemCode) ? null : "no factor"))
                .ToList();

            if (accountOptions.Count == 0)
            {
                optionsWarning = "No business partners are cached yet. Type the codes you need instead.";
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load the pickers for the item volume report");
            optionsWarning = "The pickers could not be filled from the local cache. Type the codes you need instead.";
        }
        finally
        {
            isLoadingOptions = false;
        }
    }

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

        try
        {
            var result = await Mediator.Send(
                new GetItemVolumeSalesReportQuery(
                    fromDate,
                    toDate,
                    grouping,
                    string.Join(",", selectedAccounts),
                    string.Join(",", selectedItems)),
                cancellationToken);

            if (cancellationToken.IsCancellationRequested || isDisposed)
            {
                return;
            }

            result.SwitchFirst(
                value => report = value,
                error =>
                {
                    report = null;
                    errorMessage = error.Description;
                });

            if (!result.IsError && !hasLoggedView)
            {
                hasLoggedView = true;
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

    protected void ResetFilters()
    {
        fromDate = DateTime.UtcNow.Date.AddDays(-30);
        toDate = DateTime.UtcNow.Date;
        grouping = ItemVolumeSalesGrouping.Monthly;
        selectedAccounts = new List<string>();
        selectedItems = new List<string>();
        report = null;
        errorMessage = null;
    }

    protected void OnAccountsChanged(IReadOnlyList<string> accounts) => selectedAccounts = accounts;

    protected void OnItemsChanged(IReadOnlyList<string> items) => selectedItems = items;

    protected void OnGroupingChanged(ChangeEventArgs args)
    {
        if (Enum.TryParse<ItemVolumeSalesGrouping>(args.Value?.ToString(), out var parsed))
        {
            grouping = parsed;
        }
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
            var bytes = ExportService.ExportItemVolumeSalesReportToExcel(report, ReportName);
            var base64 = Convert.ToBase64String(bytes);
            await JS.InvokeVoidAsync(
                "downloadFile",
                $"{ReportName.Replace(' ', '_')}_{IAuditService.ToCAT(DateTime.UtcNow):yyyyMMdd_HHmm}.xlsx",
                base64);
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

    /// <summary>Periods with nothing in them are noise on a daily grouping over a quarter.</summary>
    protected List<ItemVolumeSalesPeriodResult> VisiblePeriods => report is null
        ? new List<ItemVolumeSalesPeriodResult>()
        : report.Periods
            .Where(period => period.InvoiceCount > 0 || period.CreditNoteCount > 0)
            .OrderByDescending(period => period.PeriodStartUtc)
            .ToList();

    protected List<ItemVolumeSalesAccountResult> ActiveAccounts => report is null
        ? new List<ItemVolumeSalesAccountResult>()
        : report.AccountTotals
            .Where(account => account.InvoiceCount > 0 || account.CreditNoteCount > 0)
            .ToList();

    protected string GroupingPeriodNoun => grouping switch
    {
        ItemVolumeSalesGrouping.Daily => "days",
        ItemVolumeSalesGrouping.Weekly => "weeks",
        _ => "months"
    };

    protected string FormattedRange => fromDate.HasValue && toDate.HasValue
        ? $"{fromDate.Value:dd MMM yyyy} – {toDate.Value:dd MMM yyyy}"
        : "no range";

    protected string FormatGeneratedAt() =>
        report is null || report.GeneratedAtUtc == default
            ? string.Empty
            : IAuditService.ToCAT(report.GeneratedAtUtc).ToString("dd MMM yyyy HH:mm 'CAT'");

    /// <summary>
    /// A currency pair, dropping whichever side is zero. Both sides show when the
    /// window traded in neither, so a genuinely empty result still reads as money.
    /// </summary>
    protected static string FormatMoney(decimal usd, decimal zig)
    {
        var parts = new List<string>();

        if (usd != 0 || zig == 0)
        {
            parts.Add($"USD {usd:N2}");
        }

        if (zig != 0)
        {
            parts.Add($"ZiG {zig:N2}");
        }

        return string.Join(" • ", parts);
    }

    protected static string FormatQuantity(decimal value) => value.ToString("N2");

    protected static string FormatVolume(decimal value) => value.ToString("N3");

    protected static string FormatFactor(decimal? factor) => factor.HasValue ? factor.Value.ToString("0.######") : "—";

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
