using ErrorOr;
using MediatR;
using Microsoft.Extensions.Caching.Memory;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.Invoices.Queries.GetPagedInvoices;

/// <summary>
/// One page of the Invoices list.
/// </summary>
/// <remarks>
/// SAP filters, counts, totals and pages it, except for the fiscal state, which SAP does not hold: the
/// fiscal log here decides it. So a fiscal filter, or "select every invoice still to fiscalise", reads
/// the matches from SAP (up to <see cref="ScanLimit"/>, as the page itself used to), looks up their
/// fiscal state, and keeps that scan for <see cref="ScanLifetime"/> so turning pages does not read SAP
/// again. The page used to load the whole 5,000 into every open tab and do all of this itself.
/// </remarks>
public sealed class GetPagedInvoicesHandler(
    ApplicationDbContext dbContext,
    ISAPServiceLayerClient sapClient,
    IInvoiceFiscalStatusBackfillQueue fiscalStatusBackfillQueue,
    IMemoryCache cache,
    IOptions<SAPSettings> settings,
    IOptions<FiscalisationSettings> fiscalisationSettings,
    ILogger<GetPagedInvoicesHandler> logger
) : IRequestHandler<GetPagedInvoicesQuery, ErrorOr<InvoiceListResponseDto>>
{
    internal const int ScanLimit = 5000;
    internal static readonly TimeSpan ScanLifetime = TimeSpan.FromMinutes(2);

    public async Task<ErrorOr<InvoiceListResponseDto>> Handle(
        GetPagedInvoicesQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.Invoice.SapDisabled;

        if (request.Page < 1)
            return Errors.Invoice.InvalidPage;

        var hasFilters = request.DocNum.HasValue || !string.IsNullOrEmpty(request.CardCode) || request.FromDate.HasValue
            || request.ToDate.HasValue || request.VanSalesOnly.HasValue || !string.IsNullOrWhiteSpace(request.Search)
            || !string.IsNullOrWhiteSpace(request.FiscalStatus);
        var maxPageSize = hasFilters ? ScanLimit : 200;

        if (!request.FiscalisableOnly && (request.PageSize < 1 || request.PageSize > maxPageSize))
            return Errors.Invoice.InvalidPageSize(maxPageSize);

        try
        {
            return string.IsNullOrWhiteSpace(request.FiscalStatus) && !request.FiscalisableOnly
                ? await GetSapPageAsync(request, cancellationToken)
                : await GetScannedPageAsync(request, cancellationToken);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            logger.LogError(ex, "Timeout connecting to SAP Service Layer");
            return Errors.Invoice.SapTimeout;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Network error connecting to SAP Service Layer");
            return Errors.Invoice.SapConnectionError(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving paged invoices");
            return Errors.Invoice.CreationFailed(ex.Message);
        }
    }

    private async Task<InvoiceListResponseDto> GetSapPageAsync(GetPagedInvoicesQuery request, CancellationToken cancellationToken)
    {
        var skip = (request.Page - 1) * request.PageSize;
        var invoices = await sapClient.GetPagedInvoicesByOffsetAsync(skip, request.PageSize, request.DocNum, request.CardCode,
            request.FromDate, request.ToDate, request.VanSalesOnly, cancellationToken: cancellationToken, search: request.Search);
        var totalCount = await sapClient.GetInvoicesCountAsync(request.DocNum, request.CardCode, request.FromDate, request.ToDate,
            request.VanSalesOnly, cancellationToken, request.Search);
        var invoiceDtos = invoices.ToDto();

        await DescribeAsync(invoiceDtos, cancellationToken);
        QueueUnknownForBackfill(invoiceDtos, request.Page);

        InvoiceListSummaryDto? summary = null;
        if (request.IncludeSummary)
        {
            var totals = await sapClient.SummarizeInvoicesAsync(request.DocNum, request.CardCode, request.FromDate, request.ToDate,
                request.VanSalesOnly, request.Search, cancellationToken);
            summary = new InvoiceListSummaryDto
            {
                Count = totals.Count,
                Total = totals.Total,
                Vat = totals.Vat,
                Customers = totals.Customers
            };
        }

        logger.LogInformation("Retrieved page {Page} of invoices ({Count} records, total: {Total})", request.Page, invoices.Count, totalCount);

        return new InvoiceListResponseDto
        {
            Page = request.Page,
            PageSize = request.PageSize,
            Count = invoices.Count,
            TotalCount = totalCount,
            TotalPages = (int)Math.Ceiling(totalCount / (double)request.PageSize),
            HasMore = invoices.Count == request.PageSize,
            Invoices = invoiceDtos,
            Summary = summary
        };
    }

    private async Task<InvoiceListResponseDto> GetScannedPageAsync(GetPagedInvoicesQuery request, CancellationToken cancellationToken)
    {
        var scan = await GetScanAsync(request, cancellationToken);

        IEnumerable<InvoiceDto> matching = scan.Invoices;
        if (!string.IsNullOrWhiteSpace(request.FiscalStatus))
        {
            var wanted = FiscalStatusFilter.Normalize(request.FiscalStatus);
            matching = matching.Where(invoice => FiscalStatusFilter.Normalize(invoice.FiscalizationStatus) == wanted);
        }

        var invoices = matching.ToList();
        var fiscalisable = invoices.Where(CanFiscalise).ToList();

        if (request.FiscalisableOnly)
        {
            return new InvoiceListResponseDto
            {
                Page = 1,
                PageSize = fiscalisable.Count,
                Count = fiscalisable.Count,
                TotalCount = fiscalisable.Count,
                TotalPages = 1,
                Invoices = fiscalisable,
                ScanLimitReached = scan.LimitReached
            };
        }

        var pageRows = invoices.Skip((request.Page - 1) * request.PageSize).Take(request.PageSize).ToList();
        QueueUnknownForBackfill(pageRows, request.Page);

        return new InvoiceListResponseDto
        {
            Page = request.Page,
            PageSize = request.PageSize,
            Count = pageRows.Count,
            TotalCount = invoices.Count,
            TotalPages = (int)Math.Ceiling(invoices.Count / (double)request.PageSize),
            HasMore = request.Page * request.PageSize < invoices.Count,
            Invoices = pageRows,
            ScanLimitReached = scan.LimitReached,
            Summary = request.IncludeSummary
                ? new InvoiceListSummaryDto
                {
                    Count = invoices.Count,
                    Total = invoices.Sum(invoice => invoice.DocTotal),
                    Vat = invoices.Sum(invoice => invoice.VatSum),
                    Customers = invoices.Select(invoice => invoice.CardCode).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    FiscalisableCount = fiscalisable.Count
                }
                : null
        };
    }

    /// <summary>
    /// Every match the SAP filters allow, newest first, with its fiscal state. Kept briefly so the pages
    /// of one result come from one read; an explicit refresh, or a filter change, reads SAP again.
    /// </summary>
    private async Task<InvoiceScan> GetScanAsync(GetPagedInvoicesQuery request, CancellationToken cancellationToken)
    {
        var key = string.Join('|', "invoice-scan", request.DocNum, request.CardCode?.Trim().ToUpperInvariant(),
            request.FromDate?.ToString("yyyyMMdd"), request.ToDate?.ToString("yyyyMMdd"), request.VanSalesOnly,
            request.Search?.Trim().ToUpperInvariant());

        if (!request.RefreshScan && cache.TryGetValue(key, out InvoiceScan? cached) && cached is not null)
            return cached;

        var invoices = (await sapClient.GetPagedInvoicesByOffsetAsync(0, ScanLimit, request.DocNum, request.CardCode,
            request.FromDate, request.ToDate, request.VanSalesOnly, cancellationToken: cancellationToken, search: request.Search)).ToDto();
        await DescribeAsync(invoices, cancellationToken);

        var scan = new InvoiceScan(invoices, invoices.Count >= ScanLimit);
        cache.Set(key, scan, ScanLifetime);

        logger.LogInformation("Scanned {Count} invoices for a fiscal filter (limit reached: {LimitReached})", invoices.Count, scan.LimitReached);
        return scan;
    }

    private async Task DescribeAsync(List<InvoiceDto> invoices, CancellationToken cancellationToken)
    {
        await FiscalDocumentStatusProjector.EnrichInvoicesAsync(dbContext, invoices, cancellationToken);

        foreach (var invoice in invoices)
        {
            invoice.IsRepostedAfterSapUpdate = RepostedInvoiceMarker.IsReposted(fiscalisationSettings.Value, invoice.Comments);
        }
    }

    private void QueueUnknownForBackfill(List<InvoiceDto> invoices, int page)
    {
        if (!fiscalisationSettings.Value.Enabled)
            return;

        var queuedCount = InvoiceFiscalTransactionSync.QueueUnknownInvoicesForBackfill(invoices, fiscalStatusBackfillQueue);
        if (queuedCount > 0)
        {
            logger.LogInformation(
                "Queued {Count} invoice(s) on page {Page} for fiscal status backfill; they report as Unknown until the fiscal platform has been read back",
                queuedCount,
                page);
        }
    }

    /// <summary>The page's own rule for offering Fiscalise: posted, not reposted, not fiscalised yet.</summary>
    internal static bool CanFiscalise(InvoiceDto invoice) =>
        invoice.DocEntry > 0
        && !invoice.IsRepostedAfterSapUpdate
        && FiscalStatusFilter.Normalize(invoice.FiscalizationStatus) != FiscalStatusFilter.Fiscalised;

    private sealed record InvoiceScan(List<InvoiceDto> Invoices, bool LimitReached);
}
