using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common;
using ShopInventory.Common.Caching;
using ShopInventory.Common.Crates;
using ShopInventory.Common.Mobile;
using ShopInventory.Common.Pods;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.Invoices.Queries.GetPodUploadStatus;

public sealed class GetPodUploadStatusHandler(
    ISAPServiceLayerClient sapClient,
    IDocumentService documentService,
    ApplicationDbContext context,
    IOptions<SAPSettings> settings,
    IOptions<CreditNoteSyncSettings> creditNoteSyncSettings,
    IPodReportCacheStore reportCache,
    ILogger<GetPodUploadStatusHandler> logger
) : IRequestHandler<GetPodUploadStatusQuery, ErrorOr<PodUploadStatusReportDto>>
{
    private const decimal FullCreditTolerance = 0.01m;
    private static readonly TimeSpan CreditNoteEnrichmentTimeout = TimeSpan.FromSeconds(45);
    private static readonly HashSet<string> CrateInvoiceItemCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "CRA001",
        "CRA002",
        "CRA003",
        "CRA006"
    };

    public async Task<ErrorOr<PodUploadStatusReportDto>> Handle(
        GetPodUploadStatusQuery request,
        CancellationToken cancellationToken)
    {
        if (request.FromDate > request.ToDate)
            return Errors.Invoice.InvalidDateRange;

        PodReportCacheSnapshot? cachedSnapshot = null;

        try
        {
            var currentUser = request.UserId.HasValue
                ? await context.Users
                    .AsNoTracking()
                    .FirstOrDefaultAsync(u => u.Id == request.UserId.Value, cancellationToken)
                : null;

            if (request.UserId.HasValue && currentUser is null)
                return Errors.Auth.UserNotFound;

            var isDriver = string.Equals(currentUser?.Role, "Driver", StringComparison.OrdinalIgnoreCase);
            HashSet<string>? assignedCustomerCodes = null;

            if (isDriver && currentUser is not null)
            {
                var effectiveCustomerCodes = await MobileAssignedCustomerScope.GetEffectiveCustomerCodesAsync(
                    context,
                    currentUser,
                    logger,
                    cancellationToken);

                if (effectiveCustomerCodes.Count == 0)
                {
                    logger.LogWarning(
                        "Driver {Username} has no assigned customer codes; returning no POD report items",
                        currentUser.Username);

                    return BuildEmptyReport(request);
                }

                assignedCustomerCodes = effectiveCustomerCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
            }

            var cacheScopeKey = BuildCacheScopeKey(
                request.IncludeCreditNoteActivity,
                assignedCustomerCodes);

            if (cacheScopeKey is not null)
            {
                cachedSnapshot = await reportCache.GetAsync(
                    request.FromDate,
                    request.ToDate,
                    cacheScopeKey,
                    cancellationToken);

                if (CanServeCachedSnapshot(cachedSnapshot))
                {
                    var freshSnapshot = cachedSnapshot!;
                    logger.LogInformation(
                        "Serving POD report for {FromDate} to {ToDate} from the local database cache refreshed at {RefreshedAtUtc}",
                        request.FromDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        request.ToDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        freshSnapshot.RefreshedAtUtc);

                    return await RefreshCachedPodStatusesAsync(freshSnapshot.Report, cancellationToken);
                }

                if (cachedSnapshot?.IsFresh == true)
                {
                    logger.LogInformation(
                        "Refreshing POD report for {FromDate} to {ToDate} because the fresh cache has incomplete credit-note data",
                        request.FromDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        request.ToDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                }
            }

            if (!settings.Value.Enabled)
            {
                if (cachedSnapshot is not null)
                {
                    return await UseStaleSnapshotAsync(
                        cachedSnapshot,
                        "SAP is disabled; invoice data may be out of date.",
                        cancellationToken);
                }

                return Errors.Invoice.SapDisabled;
            }

            var invoices = await sapClient.GetInvoiceHeadersByDateRangeAsync(
                request.FromDate,
                request.ToDate,
                PodExclusions.ExcludedCardCodes.ToList(),
                cancellationToken: cancellationToken);
            var useCreditNoteProjection =
                creditNoteSyncSettings.Value.Enabled &&
                creditNoteSyncSettings.Value.UseForPodReports;
            var creditNoteActivity = useCreditNoteProjection
                ? request.IncludeCreditNoteActivity
                    ? await GetCreditNoteActivityInvoiceLinksFromProjectionAsync(
                        request.FromDate,
                        request.ToDate,
                        cancellationToken)
                    : new CreditNoteActivityInvoiceLinks([], [], [])
                : await GetCreditNoteActivityInvoiceLinksAsync(
                    request.FromDate,
                    request.ToDate,
                    cancellationToken);
            if (request.IncludeCreditNoteActivity)
            {
                invoices = await IncludeCreditNoteActivityInvoicesAsync(
                    invoices,
                    creditNoteActivity,
                    request.FromDate,
                    request.ToDate,
                    cancellationToken);
            }

            if (assignedCustomerCodes is not null)
            {
                invoices = invoices
                    .Where(invoice =>
                        !string.IsNullOrWhiteSpace(invoice.CardCode) &&
                        assignedCustomerCodes.Contains(invoice.CardCode))
                    .ToList();
            }

            var creditNoteLookupResult = useCreditNoteProjection
                ? await GetCreditNoteLookupFromProjectionAsync(invoices, cancellationToken)
                : await GetCreditNoteLookupWithTimeoutAsync(
                    invoices,
                    request.FromDate,
                    request.ToDate,
                    creditNoteActivity.CreditNoteLines,
                    cancellationToken);
            var creditNoteLookup = creditNoteLookupResult.Lookup;

            if (request.IncludeCreditNoteActivity)
            {
                if (creditNoteLookup.Count > 0)
                {
                    var reportDocEntries = invoices
                        .Select(invoice => invoice.DocEntry)
                        .ToHashSet();

                    creditNoteLookup = creditNoteLookup
                        .Where(pair => reportDocEntries.Contains(pair.Key))
                        .ToDictionary(pair => pair.Key, pair => pair.Value);
                }
            }

            var crateInvoiceDocEntries = await GetCrateInvoiceDocEntriesFromSapAsync(invoices, cancellationToken);
            var docEntries = invoices.Select(i => i.DocEntry).ToList();
            var podLookup = await documentService.GetPodStatusByDocEntriesAsync(docEntries, cancellationToken);
            var cratePodStatusByDocNum = await GetCratePodStatusByInvoiceDocNumsAsync(
                invoices
                    .Where(invoice => crateInvoiceDocEntries.Contains(invoice.DocEntry))
                    .Select(invoice => invoice.DocNum)
                    .ToList(),
                cancellationToken);

            var items = invoices.Select(i =>
            {
                podLookup.TryGetValue(i.DocEntry, out var podInfo);
                creditNoteLookup.TryGetValue(i.DocEntry, out var creditNoteInfo);
                var isCrateInvoice = crateInvoiceDocEntries.Contains(i.DocEntry);
                var cratePodInfo = isCrateInvoice && cratePodStatusByDocNum.TryGetValue(i.DocNum, out var matchedCratePodInfo)
                    ? matchedCratePodInfo
                    : null;
                var combinedPodInfo = MergePodStatusInfo(podInfo, cratePodInfo);
                var podTypeInfo = ResolvePodTypeInfo(
                    podInfo,
                    cratePodInfo,
                    isCrateInvoice);
                var creatorLocation = PodInvoiceCreatorLocations.GetCreatorLocation(i.UserSign);

                return new PodUploadStatusItemDto
                {
                    DocEntry = i.DocEntry,
                    DocNum = i.DocNum,
                    DocDate = i.DocDate,
                    CardCode = i.CardCode,
                    CardName = i.CardName,
                    DocTotal = i.DocTotal,
                    DocCurrency = i.DocCurrency,
                    CreatedByUserId = i.UserSign,
                    CreatedByUserCode = creatorLocation?.UserName,
                    CreatedLocation = creatorLocation?.Location,
                    IsFullyCredited = creditNoteInfo?.IsFullyCredited == true,
                    CreditNoteNumber = creditNoteInfo?.CreditNoteNumbers,
                    CreditNoteReason = creditNoteInfo?.Reasons,
                    IsCrateInvoice = isCrateInvoice,
                    HasPod = combinedPodInfo is not null,
                    HasProductPod = podTypeInfo.HasProductPod,
                    HasCratePod = podTypeInfo.HasCratePod,
                    PodUploadedAt = combinedPodInfo?.UploadedAt,
                    PodUploadedBy = combinedPodInfo?.UploadedBy,
                    PodUploadedByUsers = combinedPodInfo?.UploadedByUsers
                        .Select(uploader => new PodUploadUserSummaryDto
                        {
                            Username = uploader.Username,
                            Role = uploader.Role,
                            AssignedSection = uploader.AssignedSection,
                            FileCount = uploader.FileCount,
                            LatestUploadedAt = uploader.LatestUploadedAt
                        })
                        .ToList() ?? new(),
                    PodCount = combinedPodInfo?.Count ?? 0,
                    ProductPodCount = podTypeInfo.ProductPodCount,
                    CratePodCount = podTypeInfo.CratePodCount
                };
            }).OrderByDescending(i => i.DocNum).ToList();

            var result = new PodUploadStatusReportDto
            {
                FromDate = request.FromDate.ToString("yyyy-MM-dd"),
                ToDate = request.ToDate.ToString("yyyy-MM-dd"),
                TotalInvoices = items.Count,
                UploadedCount = items.Count(i => i.HasPod),
                PendingCount = items.Count(i => !i.HasPod),
                CreditNoteDataComplete = creditNoteLookupResult.IsComplete,
                CreditNoteDataWarning = creditNoteLookupResult.Warning,
                Items = items
            };

            if (cacheScopeKey is not null && result.CreditNoteDataComplete)
            {
                await reportCache.SaveAsync(
                    request.FromDate,
                    request.ToDate,
                    cacheScopeKey,
                    result,
                    cancellationToken);
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            if (cachedSnapshot is not null)
            {
                return await UseStaleSnapshotAsync(
                    cachedSnapshot,
                    "SAP timed out; invoice data may be out of date.",
                    cancellationToken);
            }

            return Errors.Invoice.SapTimeout;
        }
        catch (HttpRequestException ex)
        {
            if (cachedSnapshot is not null)
            {
                return await UseStaleSnapshotAsync(
                    cachedSnapshot,
                    "SAP is unavailable; invoice data may be out of date.",
                    cancellationToken);
            }

            return Errors.Invoice.SapConnectionError(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating POD upload status report");
            return Errors.Invoice.CreationFailed(ex.Message);
        }
    }

    internal static string? BuildCacheScopeKey(
        bool includeCreditNoteActivity,
        IReadOnlyCollection<string>? assignedCustomerCodes)
    {
        // Credit-note activity can pull invoices from outside the selected invoice-date range.
        if (includeCreditNoteActivity)
        {
            return null;
        }

        if (assignedCustomerCodes is null)
        {
            return "global";
        }

        // Include the effective assignment set in the key. A driver's cached result therefore
        // becomes inaccessible immediately when their customer assignments change.
        var normalizedAssignments = string.Join(
            '\n',
            assignedCustomerCodes
                .Select(code => code.Trim().ToUpperInvariant())
                .Where(code => code.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(code => code, StringComparer.Ordinal));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedAssignments));
        return $"driver-{Convert.ToHexString(hash)}";
    }

    internal static bool CanServeCachedSnapshot(PodReportCacheSnapshot? snapshot) =>
        snapshot?.IsFresh == true && snapshot.Report.CreditNoteDataComplete;

    private async Task<PodUploadStatusReportDto> RefreshCachedPodStatusesAsync(
        PodUploadStatusReportDto report,
        CancellationToken cancellationToken)
    {
        var docEntries = report.Items
            .Select(item => item.DocEntry)
            .Where(docEntry => docEntry > 0)
            .Distinct()
            .ToList();
        var podLookup = await documentService.GetPodStatusByDocEntriesAsync(docEntries, cancellationToken);
        var cratePodStatusByDocNum = await GetCratePodStatusByInvoiceDocNumsAsync(
            report.Items
                .Where(item => item.IsCrateInvoice)
                .Select(item => item.DocNum)
                .ToList(),
            cancellationToken);

        foreach (var item in report.Items)
        {
            podLookup.TryGetValue(item.DocEntry, out var podInfo);
            var cratePodInfo = item.IsCrateInvoice &&
                cratePodStatusByDocNum.TryGetValue(item.DocNum, out var matchedCratePodInfo)
                    ? matchedCratePodInfo
                    : null;
            var combinedPodInfo = MergePodStatusInfo(podInfo, cratePodInfo);
            var podTypeInfo = ResolvePodTypeInfo(podInfo, cratePodInfo, item.IsCrateInvoice);

            item.HasPod = combinedPodInfo is not null;
            item.HasProductPod = podTypeInfo.HasProductPod;
            item.HasCratePod = podTypeInfo.HasCratePod;
            item.PodUploadedAt = combinedPodInfo?.UploadedAt;
            item.PodUploadedBy = combinedPodInfo?.UploadedBy;
            item.PodUploadedByUsers = combinedPodInfo?.UploadedByUsers
                .Select(uploader => new PodUploadUserSummaryDto
                {
                    Username = uploader.Username,
                    Role = uploader.Role,
                    AssignedSection = uploader.AssignedSection,
                    FileCount = uploader.FileCount,
                    LatestUploadedAt = uploader.LatestUploadedAt
                })
                .ToList() ?? [];
            item.PodCount = combinedPodInfo?.Count ?? 0;
            item.ProductPodCount = podTypeInfo.ProductPodCount;
            item.CratePodCount = podTypeInfo.CratePodCount;
        }

        if (creditNoteSyncSettings.Value.Enabled &&
            creditNoteSyncSettings.Value.UseForPodReports)
        {
            var invoiceTotals = report.Items
                .Where(item => item.DocEntry > 0)
                .GroupBy(item => item.DocEntry)
                .ToDictionary(
                    group => group.Key,
                    group => Math.Abs(group.First().DocTotal));
            var creditNoteResult = await GetCreditNoteLookupFromProjectionAsync(
                invoiceTotals,
                cancellationToken);

            foreach (var item in report.Items)
            {
                creditNoteResult.Lookup.TryGetValue(item.DocEntry, out var creditNoteInfo);
                item.IsFullyCredited = creditNoteInfo?.IsFullyCredited == true;
                item.CreditNoteNumber = creditNoteInfo?.CreditNoteNumbers;
                item.CreditNoteReason = creditNoteInfo?.Reasons;
            }

            report.CreditNoteDataComplete = creditNoteResult.IsComplete;
            report.CreditNoteDataWarning = creditNoteResult.Warning;
        }

        report.TotalInvoices = report.Items.Count;
        report.UploadedCount = report.Items.Count(item => item.HasPod);
        report.PendingCount = report.Items.Count(item => !item.HasPod);
        return report;
    }

    private async Task<PodUploadStatusReportDto> UseStaleSnapshotAsync(
        PodReportCacheSnapshot snapshot,
        string warning,
        CancellationToken cancellationToken)
    {
        logger.LogWarning(
            "Serving stale POD report data refreshed at {RefreshedAtUtc}: {Warning}",
            snapshot.RefreshedAtUtc,
            warning);

        var report = await RefreshCachedPodStatusesAsync(snapshot.Report, cancellationToken);
        report.CreditNoteDataComplete = false;
        report.CreditNoteDataWarning = warning;
        return report;
    }

    private static PodTypeInfo ResolvePodTypeInfo(
        PodStatusInfo? invoicePodInfo,
        PodStatusInfo? cratePodInfo,
        bool isCrateInvoice)
    {
        if (isCrateInvoice)
        {
            return new PodTypeInfo(
                HasProductPod: false,
                HasCratePod: cratePodInfo is not null || invoicePodInfo is not null,
                ProductPodCount: 0,
                CratePodCount: (cratePodInfo?.Count ?? 0) + (invoicePodInfo?.Count ?? 0));
        }

        return new PodTypeInfo(
            HasProductPod: invoicePodInfo is not null,
            HasCratePod: false,
            ProductPodCount: invoicePodInfo?.Count ?? 0,
            CratePodCount: 0);
    }

    private sealed record PodTypeInfo(
        bool HasProductPod,
        bool HasCratePod,
        int ProductPodCount,
        int CratePodCount);

    private sealed record CreditNoteInfo(
        string CreditNoteNumbers,
        string Reasons,
        bool IsFullyCredited);

    private sealed record CreditNoteLineInfo(
        int InvoiceDocEntry,
        int CreditNoteDocEntry,
        int CreditNoteDocNum,
        decimal CreditAmount,
        string? Reason);

    private async Task<List<Invoice>> IncludeCreditNoteActivityInvoicesAsync(
        IReadOnlyList<Invoice> invoices,
        CreditNoteActivityInvoiceLinks linkedInvoices,
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken)
    {
        var existingDocEntries = invoices
            .Where(invoice => invoice.DocEntry > 0)
            .Select(invoice => invoice.DocEntry)
            .ToHashSet();

        var existingDocNums = invoices
            .Where(invoice => invoice.DocNum > 0)
            .Select(invoice => invoice.DocNum)
            .ToHashSet();

        var missingDocEntries = linkedInvoices.DocEntries
            .Where(docEntry => !existingDocEntries.Contains(docEntry))
            .ToList();
        var missingDocNums = linkedInvoices.DocNums
            .Where(docNum => !existingDocNums.Contains(docNum))
            .ToList();

        if (missingDocEntries.Count == 0 && missingDocNums.Count == 0)
        {
            return invoices.ToList();
        }

        var excludedCardCodes = PodExclusions.ExcludedCardCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var linkedInvoicesByDocEntry = missingDocEntries.Count > 0
            ? await sapClient.GetInvoiceHeadersByDocEntriesAsync(missingDocEntries, cancellationToken)
            : [];
        var linkedInvoicesByDocNum = missingDocNums.Count > 0
            ? await sapClient.GetInvoicesByDocNumsAsync(missingDocNums, cancellationToken)
            : [];

        var eligibleLinkedInvoices = linkedInvoicesByDocEntry
            .Concat(linkedInvoicesByDocNum)
            .Where(invoice =>
                invoice.DocEntry > 0 &&
                (string.IsNullOrWhiteSpace(invoice.CardCode) || !excludedCardCodes.Contains(invoice.CardCode)))
            .GroupBy(invoice => invoice.DocEntry)
            .Select(group => group.First())
            .ToList();

        if (eligibleLinkedInvoices.Count == 0)
        {
            return invoices.ToList();
        }

        logger.LogInformation(
            "POD report included {Count} additional invoice(s) linked to credit notes dated between {FromDate} and {ToDate}",
            eligibleLinkedInvoices.Count,
            fromDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            toDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        return invoices
            .Concat(eligibleLinkedInvoices)
            .GroupBy(invoice => invoice.DocEntry)
            .Select(group => group.First())
            .ToList();
    }

    private sealed record CreditNoteActivityInvoiceLinks(
        HashSet<int> DocEntries,
        HashSet<int> DocNums,
        List<CreditNoteLineInfo> CreditNoteLines);

    private async Task<CreditNoteActivityInvoiceLinks> GetCreditNoteActivityInvoiceLinksFromProjectionAsync(
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken)
    {
        var from = fromDate.Date;
        var through = toDate.Date;
        var lines = await context.SapCreditNoteLineSnapshots
            .AsNoTracking()
            .Where(line =>
                line.BaseType == 13 &&
                line.BaseEntry.HasValue &&
                !line.CreditNote.IsCancelled &&
                line.CreditNote.DocDate >= from &&
                line.CreditNote.DocDate <= through)
            .Select(line => new CreditNoteLineInfo(
                line.BaseEntry!.Value,
                line.CreditNoteDocEntry,
                line.CreditNote.SapDocNum,
                Math.Abs(line.LineTotal + line.VatSum),
                line.CreditReason))
            .ToListAsync(cancellationToken);

        return new CreditNoteActivityInvoiceLinks(
            lines.Select(line => line.InvoiceDocEntry).ToHashSet(),
            [],
            lines);
    }

    private async Task<CreditNoteActivityInvoiceLinks> GetCreditNoteActivityInvoiceLinksAsync(
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken)
    {
        // Whole months rather than the caller's exact dates: see SqlMonthRangeCover. Every distinct
        // range picked in the UI would otherwise leave another undeletable SAP query object behind.
        // DocDate is projected so the surplus days can be dropped here.
        var rows = new List<Dictionary<string, object?>>();

        foreach (var (monthStart, monthEnd) in SqlMonthRangeCover.CoverMonths(fromDate, toDate))
        {
            var monthStartText = monthStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var monthEndText = monthEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            var sqlText = $@"
SELECT
    T0.""BaseEntry"" AS ""InvoiceDocEntry"",
    T0.""BaseRef"" AS ""InvoiceDocNum"",
    T1.""DocEntry"" AS ""CreditNoteDocEntry"",
    T1.""DocNum"" AS ""CreditNoteDocNum"",
    T1.""DocDate"" AS ""CreditNoteDocDate"",
    T0.""LineNum"" AS ""CreditLineNum"",
    T0.""LineTotal"" AS ""CreditLineTotal"",
    T0.""VatSum"" AS ""CreditVatSum"",
    T0.""U_Reasons"" AS ""CreditReason""
FROM RIN1 T0
INNER JOIN ORIN T1
        ON T1.""DocEntry"" = T0.""DocEntry""
WHERE T0.""BaseType"" = 13
  AND T1.""CANCELED"" = 'N'
  AND T1.""DocDate"" >= '{monthStartText}'
  AND T1.""DocDate"" <= '{monthEndText}'
ORDER BY T0.""BaseEntry"", T0.""BaseRef"", T1.""DocDate"", T1.""DocNum"", T0.""LineNum""";

            var monthRows = await sapClient.ExecuteScopedRawSqlQueryAsync(
                "PODCNDT",
                "POD credit note activity invoice links",
                sqlText,
                cancellationToken);

            // Drop the days the month covers but the caller did not ask for.
            foreach (var row in monthRows)
            {
                var docDate = TryGetDateTime(row, "CreditNoteDocDate");
                if (docDate is null || (docDate.Value.Date >= fromDate.Date && docDate.Value.Date <= toDate.Date))
                {
                    rows.Add(row);
                }
            }
        }

        var docEntries = rows
            .Select(row => TryGetInt32(row, "InvoiceDocEntry"))
            .Where(docEntry => docEntry.HasValue && docEntry.Value > 0)
            .Select(docEntry => docEntry!.Value)
            .ToHashSet();
        var docNums = rows
            .Select(row => TryGetInt32(row, "InvoiceDocNum"))
            .Where(docNum => docNum.HasValue && docNum.Value > 0)
            .Select(docNum => docNum!.Value)
            .ToHashSet();
        var creditNoteLines = new List<CreditNoteLineInfo>();

        foreach (var row in rows)
        {
            var invoiceDocEntry = TryGetInt32(row, "InvoiceDocEntry");
            var creditNoteDocEntry = TryGetInt32(row, "CreditNoteDocEntry");
            var creditNoteDocNum = TryGetInt32(row, "CreditNoteDocNum");

            if (!invoiceDocEntry.HasValue || invoiceDocEntry.Value <= 0 ||
                !creditNoteDocEntry.HasValue || !creditNoteDocNum.HasValue)
            {
                continue;
            }

            creditNoteLines.Add(new CreditNoteLineInfo(
                invoiceDocEntry.Value,
                creditNoteDocEntry.Value,
                creditNoteDocNum.Value,
                Math.Abs(GetDecimal(row, "CreditLineTotal") + GetDecimal(row, "CreditVatSum")),
                GetString(row, "CreditReason")));
        }

        return new CreditNoteActivityInvoiceLinks(docEntries, docNums, creditNoteLines);
    }

    private async Task<Dictionary<int, CreditNoteInfo>> GetCreditNoteLookupAsync(
        IReadOnlyList<Invoice> invoices,
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken)
    {
        var reportInvoices = invoices
            .Where(invoice => invoice.DocEntry > 0)
            .GroupBy(invoice => invoice.DocEntry)
            .Select(group => group.First())
            .ToList();

        var invoiceTotalsByDocEntry = reportInvoices
            .ToDictionary(
                invoice => invoice.DocEntry,
                GetInvoiceCreditableTotal);

        if (invoiceTotalsByDocEntry.Count == 0)
        {
            return [];
        }

        var invoiceDocEntryByDocNum = reportInvoices
            .Where(invoice => invoice.DocNum > 0)
            .GroupBy(invoice => invoice.DocNum)
            .ToDictionary(group => group.Key, group => group.First().DocEntry);

        try
        {
            var creditNoteLines = new List<CreditNoteLineInfo>();

            // The original OR combined an integer BaseEntry list with a string BaseRef list, so no
            // single range covers it. Split into one ranged query per column and union the results:
            // BaseEntry numerically, BaseRef over same-digit-width ranges so the text comparison
            // agrees with numeric order. See SqlIdRangeCover.
            var linkFilters = new List<string>();

            linkFilters.AddRange(
                SqlIdRangeCover
                    .Cover(reportInvoices.Select(invoice => invoice.DocEntry))
                    .Select(range => $@"T0.""BaseEntry"" BETWEEN {range.Start} AND {range.End}"));

            linkFilters.AddRange(
                SqlIdRangeCover
                    .CoverSameDigitWidth(reportInvoices.Select(invoice => invoice.DocNum))
                    .Select(range => $@"T0.""BaseRef"" BETWEEN '{range.Start}' AND '{range.End}'"));

            // A credit note line can satisfy both predicates; keep it once.
            var seenCreditNoteLines = new HashSet<(int CreditNoteDocEntry, int CreditLineNum)>();

            foreach (var linkFilter in linkFilters)
            {
                var sqlText = $@"
SELECT
    T0.""BaseEntry"" AS ""InvoiceDocEntry"",
    T0.""BaseRef"" AS ""InvoiceDocNum"",
    T1.""DocEntry"" AS ""CreditNoteDocEntry"",
    T1.""DocNum"" AS ""CreditNoteDocNum"",
    T0.""LineNum"" AS ""CreditLineNum"",
    T0.""LineTotal"" AS ""CreditLineTotal"",
    T0.""VatSum"" AS ""CreditVatSum"",
    T0.""U_Reasons"" AS ""CreditReason""
FROM RIN1 T0
INNER JOIN ORIN T1
        ON T1.""DocEntry"" = T0.""DocEntry""
WHERE T0.""BaseType"" = 13
  AND {linkFilter}
  AND T1.""CANCELED"" = 'N'
ORDER BY T0.""BaseEntry"", T0.""BaseRef"", T1.""DocDate"", T1.""DocNum"", T0.""LineNum""";

                var rows = await sapClient.ExecuteScopedRawSqlQueryAsync(
                    "PODCN",
                    "POD credit note links",
                    sqlText,
                    cancellationToken);

                foreach (var row in rows)
                {
                    var invoiceDocEntry = TryGetInt32(row, "InvoiceDocEntry");
                    if (!invoiceDocEntry.HasValue || !invoiceTotalsByDocEntry.ContainsKey(invoiceDocEntry.Value))
                    {
                        var invoiceDocNum = TryGetInt32(row, "InvoiceDocNum");
                        if (!invoiceDocNum.HasValue ||
                            !invoiceDocEntryByDocNum.TryGetValue(invoiceDocNum.Value, out var resolvedDocEntry))
                        {
                            continue;
                        }

                        invoiceDocEntry = resolvedDocEntry;
                    }

                    var creditNoteDocEntry = TryGetInt32(row, "CreditNoteDocEntry");
                    var creditNoteDocNum = TryGetInt32(row, "CreditNoteDocNum");

                    if (!creditNoteDocEntry.HasValue ||
                        !creditNoteDocNum.HasValue)
                    {
                        continue;
                    }

                    // Both ranged queries can return the same RIN1 line; counting it twice would
                    // double the credited amount.
                    var lineKey = (creditNoteDocEntry.Value, TryGetInt32(row, "CreditLineNum") ?? -1);
                    if (!seenCreditNoteLines.Add(lineKey))
                    {
                        continue;
                    }

                    creditNoteLines.Add(new CreditNoteLineInfo(
                        invoiceDocEntry.Value,
                        creditNoteDocEntry.Value,
                        creditNoteDocNum.Value,
                        Math.Abs(GetDecimal(row, "CreditLineTotal") + GetDecimal(row, "CreditVatSum")),
                        GetString(row, "CreditReason")));
                }
            }

            return BuildCreditNoteInfoLookup(creditNoteLines, invoiceTotalsByDocEntry);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "POD report credit-note SQL lookup failed; falling back to SAP CreditNotes entity lookup for invoices from {FromDate} to {ToDate}",
                fromDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                toDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

            try
            {
                return await GetCreditNoteLookupFromCreditNotesApiAsync(
                    reportInvoices,
                    invoiceTotalsByDocEntry,
                    fromDate,
                    toDate,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception fallbackEx)
            {
                logger.LogError(fallbackEx, "POD report credit-note fallback lookup failed; continuing without credit-note enrichment");
                return [];
            }
        }
    }

    private async Task<CreditNoteLookupResult> GetCreditNoteLookupWithTimeoutAsync(
        IReadOnlyList<Invoice> invoices,
        DateTime fromDate,
        DateTime toDate,
        IReadOnlyList<CreditNoteLineInfo> dateRangeCreditNoteLines,
        CancellationToken requestCancellationToken)
    {
        var invoiceTotalsByDocEntry = invoices
            .Where(invoice => invoice.DocEntry > 0)
            .GroupBy(invoice => invoice.DocEntry)
            .ToDictionary(group => group.Key, group => GetInvoiceCreditableTotal(group.First()));
        var dateRangeLookup = BuildCreditNoteInfoLookup(
            dateRangeCreditNoteLines,
            invoiceTotalsByDocEntry);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(requestCancellationToken);
        timeoutCts.CancelAfter(CreditNoteEnrichmentTimeout);

        try
        {
            var lookup = await GetCreditNoteLookupAsync(
                invoices,
                fromDate,
                toDate,
                timeoutCts.Token);

            foreach (var pair in dateRangeLookup)
            {
                lookup.TryAdd(pair.Key, pair.Value);
            }

            return new CreditNoteLookupResult(lookup, true, null);
        }
        catch (OperationCanceledException) when (!requestCancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "POD report full credit-note enrichment exceeded {TimeoutSeconds} seconds; continuing with {CreditNoteCount} credit-note entries from the date-range query",
                CreditNoteEnrichmentTimeout.TotalSeconds,
                dateRangeLookup.Count);
            return new CreditNoteLookupResult(
                dateRangeLookup,
                false,
                $"Credit-note enrichment exceeded {CreditNoteEnrichmentTimeout.TotalSeconds:0} seconds. " +
                "Credit notes outside the selected date range may be missing from this report.");
        }
    }

    private async Task<CreditNoteLookupResult> GetCreditNoteLookupFromProjectionAsync(
        IReadOnlyList<Invoice> invoices,
        CancellationToken cancellationToken)
    {
        var invoiceTotals = invoices
            .Where(invoice => invoice.DocEntry > 0)
            .GroupBy(invoice => invoice.DocEntry)
            .ToDictionary(
                group => group.Key,
                group => GetInvoiceCreditableTotal(group.First()));

        return await GetCreditNoteLookupFromProjectionAsync(invoiceTotals, cancellationToken);
    }

    private async Task<CreditNoteLookupResult> GetCreditNoteLookupFromProjectionAsync(
        IReadOnlyDictionary<int, decimal> invoiceTotalsByDocEntry,
        CancellationToken cancellationToken)
    {
        if (invoiceTotalsByDocEntry.Count == 0)
        {
            return new CreditNoteLookupResult([], true, null);
        }

        var invoiceDocEntries = invoiceTotalsByDocEntry.Keys.ToList();
        var creditNoteLines = await context.SapCreditNoteLineSnapshots
            .AsNoTracking()
            .Where(line =>
                line.BaseType == 13 &&
                line.BaseEntry.HasValue &&
                invoiceDocEntries.Contains(line.BaseEntry.Value) &&
                !line.CreditNote.IsCancelled)
            .Select(line => new CreditNoteLineInfo(
                line.BaseEntry!.Value,
                line.CreditNoteDocEntry,
                line.CreditNote.SapDocNum,
                Math.Abs(line.LineTotal + line.VatSum),
                line.CreditReason))
            .ToListAsync(cancellationToken);

        var lookup = BuildCreditNoteInfoLookup(
            creditNoteLines,
            invoiceTotalsByDocEntry);
        var syncState = await context.CacheSyncStates
            .AsNoTracking()
            .SingleOrDefaultAsync(
                state => state.CacheKey == CreditNoteProjectionSyncService.CacheKey,
                cancellationToken);
        var now = DateTime.UtcNow;
        var isFresh = IsCreditNoteProjectionFresh(
            syncState,
            creditNoteSyncSettings.Value,
            now);

        if (isFresh)
        {
            return new CreditNoteLookupResult(lookup, true, null);
        }

        var warning = syncState?.LastSyncedAt is { } staleAt
            ? $"Credit-note synchronization was last completed at {staleAt:yyyy-MM-dd HH:mm} UTC. " +
              "Some credit-note statuses are not yet verified."
            : "Credit-note synchronization is still preparing its initial local projection. " +
              "Some credit-note statuses are not yet verified.";

        return new CreditNoteLookupResult(lookup, false, warning);
    }

    internal static bool IsCreditNoteProjectionFresh(
        CacheSyncStateEntity? syncState,
        CreditNoteSyncSettings settings,
        DateTime nowUtc)
    {
        if (syncState?.LastSyncedAt is not { } lastSyncedAt)
        {
            return false;
        }

        var hasCurrentError = syncState.LastErrorAt.HasValue &&
            syncState.LastErrorAt.Value >= lastSyncedAt;
        return !hasCurrentError &&
            nowUtc - lastSyncedAt <= TimeSpan.FromMinutes(
                Math.Max(1, settings.StaleAfterMinutes));
    }

    private async Task<Dictionary<int, CreditNoteInfo>> GetCreditNoteLookupFromCreditNotesApiAsync(
        IReadOnlyList<Invoice> reportInvoices,
        IReadOnlyDictionary<int, decimal> invoiceTotalsByDocEntry,
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken)
    {
        if (reportInvoices.Count == 0)
        {
            return [];
        }

        var invoiceDocEntries = invoiceTotalsByDocEntry.Keys.ToHashSet();
        var fallbackToDate = DateTime.Today > toDate.Date ? DateTime.Today : toDate.Date;
        var creditNotes = await sapClient.GetCreditNotesByDateRangeAsync(
            fromDate.Date,
            fallbackToDate,
            cancellationToken);

        var creditNoteLines = new List<CreditNoteLineInfo>();

        foreach (var creditNote in creditNotes.Where(note => !IsCanceled(note.Cancelled)))
        {
            var baseInvoiceLines = creditNote.DocumentLines?
                .Where(line =>
                    line.BaseType == 13 &&
                    line.BaseEntry.HasValue &&
                    invoiceDocEntries.Contains(line.BaseEntry.Value))
                .ToList();

            if (baseInvoiceLines is null || baseInvoiceLines.Count == 0)
            {
                continue;
            }

            var linkedInvoiceCount = baseInvoiceLines
                .Select(line => line.BaseEntry!.Value)
                .Distinct()
                .Count();

            foreach (var invoiceGroup in baseInvoiceLines.GroupBy(line => line.BaseEntry!.Value))
            {
                var creditAmount = linkedInvoiceCount == 1
                    ? Math.Abs(creditNote.DocTotal)
                    : invoiceGroup.Sum(line => Math.Abs(line.LineTotal + line.VatSum));

                var reasons = invoiceGroup
                    .Select(line => line.CreditReason?.Trim())
                    .Where(reason => !string.IsNullOrWhiteSpace(reason))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                creditNoteLines.Add(new CreditNoteLineInfo(
                    invoiceGroup.Key,
                    creditNote.DocEntry,
                    creditNote.DocNum,
                    creditAmount,
                    string.Join("; ", reasons)));
            }
        }

        return BuildCreditNoteInfoLookup(creditNoteLines, invoiceTotalsByDocEntry);
    }

    private static Dictionary<int, CreditNoteInfo> BuildCreditNoteInfoLookup(
        IEnumerable<CreditNoteLineInfo> creditNoteLines,
        IReadOnlyDictionary<int, decimal> invoiceTotalsByDocEntry)
    {
        return creditNoteLines
            .GroupBy(line => line.InvoiceDocEntry)
            .Where(group => invoiceTotalsByDocEntry.ContainsKey(group.Key))
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var invoiceTotal = invoiceTotalsByDocEntry[group.Key];
                    var creditedTotal = GetCreditedTotal(group);
                    var creditNoteNumbers = group
                        .GroupBy(line => line.CreditNoteDocEntry)
                        .Select(noteGroup => noteGroup.First().CreditNoteDocNum)
                        .Select(docNum => docNum.ToString(CultureInfo.InvariantCulture))
                        .ToList();

                    var reasons = group
                        .Select(line => line.Reason?.Trim())
                        .Where(reason => !string.IsNullOrWhiteSpace(reason))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    return new CreditNoteInfo(
                        string.Join(", ", creditNoteNumbers),
                        string.Join("; ", reasons),
                        IsFullyCredited(invoiceTotal, creditedTotal));
                });
    }

    private static decimal GetCreditedTotal(IEnumerable<CreditNoteLineInfo> creditNoteLines) =>
        creditNoteLines.Sum(line => line.CreditAmount);

    private static decimal GetInvoiceCreditableTotal(Invoice invoice)
    {
        var docTotal = Math.Abs(invoice.DocTotal);
        if (docTotal > 0)
        {
            return docTotal;
        }

        var lineTotal = invoice.DocumentLines?
            .Where(line =>
                line.LineTotal != 0 ||
                !string.IsNullOrWhiteSpace(line.ItemCode) ||
                !string.IsNullOrWhiteSpace(line.ItemDescription))
            .Sum(line => Math.Abs(line.LineTotal)) ?? 0m;

        return lineTotal;
    }

    private static bool IsFullyCredited(decimal invoiceTotal, decimal creditedTotal) =>
        invoiceTotal > 0 && creditedTotal + FullCreditTolerance >= invoiceTotal;

    private static bool IsCanceled(string? canceled) =>
        string.Equals(canceled, "tYES", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(canceled, "Y", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(canceled, "Yes", StringComparison.OrdinalIgnoreCase);

    private static int? TryGetInt32(IReadOnlyDictionary<string, object?> row, string key)
    {
        var value = GetValue(row, key);

        return value switch
        {
            null => null,
            int intValue => intValue,
            long longValue when longValue is >= int.MinValue and <= int.MaxValue => (int)longValue,
            decimal decimalValue => (int)decimalValue,
            double doubleValue => (int)doubleValue,
            _ when int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private static decimal GetDecimal(IReadOnlyDictionary<string, object?> row, string key)
    {
        var value = GetValue(row, key);

        return value switch
        {
            null => 0m,
            decimal decimalValue => decimalValue,
            int intValue => intValue,
            long longValue => longValue,
            double doubleValue => (decimal)doubleValue,
            float floatValue => (decimal)floatValue,
            _ when decimal.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0m
        };
    }

    private static string? GetString(IReadOnlyDictionary<string, object?> row, string key) =>
        GetValue(row, key)?.ToString()?.Trim();

    /// <summary>
    /// Reads a SAP date column. Returns null when it is absent or unparseable, which callers treat
    /// as "do not filter on it" rather than as an exclusion.
    /// </summary>
    private static DateTime? TryGetDateTime(IReadOnlyDictionary<string, object?> row, string key)
    {
        var value = GetValue(row, key);

        return value switch
        {
            null => null,
            DateTime dateTimeValue => dateTimeValue,
            DateTimeOffset dateTimeOffsetValue => dateTimeOffsetValue.DateTime,
            _ when DateTime.TryParse(
                value.ToString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed) => parsed,
            _ => null
        };
    }

    private static object? GetValue(IReadOnlyDictionary<string, object?> row, string key)
    {
        if (row.TryGetValue(key, out var value))
        {
            return value;
        }

        foreach (var pair in row)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
    }

    private async Task<HashSet<int>> GetCrateInvoiceDocEntriesFromSapAsync(
        IReadOnlyCollection<Invoice> invoices,
        CancellationToken cancellationToken)
    {
        var crateInvoiceDocEntries = invoices
            .Where(invoice => invoice.DocEntry > 0 && HasCrateInvoiceLine(invoice.DocumentLines))
            .Select(invoice => invoice.DocEntry)
            .ToHashSet();

        var unresolvedDocEntries = invoices
            .Where(invoice =>
                invoice.DocEntry > 0 &&
                (invoice.DocumentLines is null || invoice.DocumentLines.Count == 0))
            .Select(invoice => invoice.DocEntry)
            .Distinct()
            .ToList();

        // Ranges rather than the exact doc entries: see SqlIdRangeCover. A range recurs across
        // requests, so SAP reuses one query object instead of accumulating one per request.
        var requestedDocEntries = unresolvedDocEntries.ToHashSet();
        var rangeIndex = 0;
        try
        {
            foreach (var (start, end) in SqlIdRangeCover.Cover(unresolvedDocEntries))
            {
                rangeIndex++;
                var rows = await sapClient.ExecuteScopedRawSqlQueryAsync(
                    "PODCRA",
                    "POD SAP crate invoice classification",
                    BuildCrateInvoiceClassificationSql(start, end),
                    cancellationToken);

                foreach (var row in rows)
                {
                    var invoiceDocEntry = TryGetInt32(row, "InvoiceDocEntry");

                    // The range covers ids nobody asked about; keep only the requested ones.
                    if (invoiceDocEntry.HasValue && requestedDocEntries.Contains(invoiceDocEntry.Value))
                    {
                        crateInvoiceDocEntries.Add(invoiceDocEntry.Value);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "SAP SQL crate-invoice classification failed after {CompletedRangeCount} range(s); falling back to the Invoices API for {InvoiceCount} unresolved invoices",
                rangeIndex,
                unresolvedDocEntries.Count);

            var invoicesWithLines = await sapClient.GetInvoiceHeadersByDocEntriesAsync(
                unresolvedDocEntries,
                cancellationToken);

            foreach (var invoice in invoicesWithLines.Where(invoice => HasCrateInvoiceLine(invoice.DocumentLines)))
            {
                crateInvoiceDocEntries.Add(invoice.DocEntry);
            }
        }

        logger.LogInformation(
            "SAP classified {CrateInvoiceCount} of {InvoiceCount} POD report invoices as crate invoices using item codes {CrateItemCodes}",
            crateInvoiceDocEntries.Count,
            invoices.Count,
            string.Join(", ", CrateInvoiceItemCodes.OrderBy(code => code, StringComparer.OrdinalIgnoreCase)));

        return crateInvoiceDocEntries;
    }

    private static bool HasCrateInvoiceLine(IEnumerable<InvoiceLine>? documentLines) =>
        documentLines?.Any(line =>
            !string.IsNullOrWhiteSpace(line.ItemCode) &&
            CrateInvoiceItemCodes.Contains(line.ItemCode.Trim())) == true;

    /// <summary>
    /// Classification SQL for one aligned slice of DocEntry space. The item codes are a compile-time
    /// constant, so the whole statement is fixed once the range is chosen.
    /// </summary>
    private static string BuildCrateInvoiceClassificationSql(int docEntryStart, int docEntryEnd)
    {
        var itemCodeFilter = string.Join(", ", CrateInvoiceItemCodes
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .Select(code => $"'{code}'"));

        return $@"
SELECT DISTINCT
    T0.""DocEntry"" AS ""InvoiceDocEntry""
FROM INV1 T0
WHERE T0.""DocEntry"" BETWEEN {docEntryStart} AND {docEntryEnd}
  AND T0.""ItemCode"" IN ({itemCodeFilter})
ORDER BY T0.""DocEntry""";
    }

    private async Task<Dictionary<int, PodStatusInfo>> GetCratePodStatusByInvoiceDocNumsAsync(
        List<int> invoiceDocNums,
        CancellationToken cancellationToken)
    {
        var requestedDocNums = invoiceDocNums
            .Where(invoiceDocNum => invoiceDocNum > 0)
            .Distinct()
            .ToList();

        if (requestedDocNums.Count == 0)
        {
            return [];
        }

        var latestTransactions = await context.CrateTransactions
            .AsNoTracking()
            .Where(transaction =>
                EF.Functions.ILike(transaction.TransactionType, CrateTrackingConstants.TransactionTypeInvoice) &&
                transaction.InvoiceDocNum.HasValue &&
                requestedDocNums.Contains(transaction.InvoiceDocNum.Value))
            .OrderByDescending(transaction => transaction.EffectiveDate)
            .ThenByDescending(transaction => transaction.CreatedAt)
            .Select(transaction => new
            {
                transaction.Id,
                InvoiceDocNum = transaction.InvoiceDocNum!.Value
            })
            .ToListAsync(cancellationToken);

        var latestTransactionsByDocNum = latestTransactions
            .GroupBy(transaction => transaction.InvoiceDocNum)
            .ToDictionary(group => group.Key, group => group.First());

        if (latestTransactionsByDocNum.Count == 0)
        {
            return [];
        }

        var transactionIds = latestTransactionsByDocNum.Values
            .Select(transaction => transaction.Id)
            .ToList();

        var submissions = await context.CratePodSubmissions
            .AsNoTracking()
            .Where(submission => transactionIds.Contains(submission.CrateTransactionId))
            .Select(submission => new
            {
                submission.Id,
                submission.CrateTransactionId,
                Username = submission.SubmittedByUser != null ? submission.SubmittedByUser.Username : null,
                Role = submission.SubmittedByUser != null ? submission.SubmittedByUser.Role : null,
                AssignedSection = submission.SubmittedByUser != null ? submission.SubmittedByUser.AssignedSection : null
            })
            .ToListAsync(cancellationToken);

        if (submissions.Count == 0)
        {
            return [];
        }

        var submissionIds = submissions
            .Select(submission => submission.Id)
            .ToList();

        var attachments = await context.DocumentAttachments
            .AsNoTracking()
            .Where(attachment =>
                attachment.EntityType == CrateTrackingConstants.AttachmentEntityTypeCratePodSubmission &&
                submissionIds.Contains(attachment.EntityId))
            .Select(attachment => new
            {
                SubmissionId = attachment.EntityId,
                attachment.UploadedAt
            })
            .ToListAsync(cancellationToken);

        if (attachments.Count == 0)
        {
            return [];
        }

        var transactionDocNumsById = latestTransactionsByDocNum.Values
            .ToDictionary(transaction => transaction.Id, transaction => transaction.InvoiceDocNum);

        var cratePodData = submissions
            .GroupJoin(
                attachments,
                submission => submission.Id,
                attachment => attachment.SubmissionId,
                (submission, submissionAttachments) => new
                {
                    submission.CrateTransactionId,
                    submission.Username,
                    submission.Role,
                    submission.AssignedSection,
                    Attachments = submissionAttachments.ToList()
                })
            .Where(submission =>
                submission.Attachments.Count > 0 &&
                transactionDocNumsById.ContainsKey(submission.CrateTransactionId))
            .GroupBy(submission => transactionDocNumsById[submission.CrateTransactionId])
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var uploadedByUsers = group
                        .Select(submission =>
                        {
                            var latestUpload = submission.Attachments
                                .OrderByDescending(attachment => attachment.UploadedAt)
                                .First();

                            return new PodUploadUserSummaryInfo
                            {
                                Username = string.IsNullOrWhiteSpace(submission.Username)
                                    ? "Unknown uploader"
                                    : submission.Username.Trim(),
                                Role = string.IsNullOrWhiteSpace(submission.Role)
                                    ? null
                                    : submission.Role.Trim(),
                                AssignedSection = string.IsNullOrWhiteSpace(submission.AssignedSection)
                                    ? null
                                    : submission.AssignedSection.Trim(),
                                FileCount = submission.Attachments.Count,
                                LatestUploadedAt = latestUpload.UploadedAt
                            };
                        })
                        .GroupBy(summary => summary.Username, StringComparer.OrdinalIgnoreCase)
                        .Select(uploaderGroup =>
                        {
                            var latestUpload = uploaderGroup
                                .Where(summary => summary.LatestUploadedAt.HasValue)
                                .OrderByDescending(summary => summary.LatestUploadedAt)
                                .FirstOrDefault() ?? uploaderGroup.First();

                            return new PodUploadUserSummaryInfo
                            {
                                Username = uploaderGroup.Key,
                                Role = latestUpload.Role,
                                AssignedSection = latestUpload.AssignedSection,
                                FileCount = uploaderGroup.Sum(summary => summary.FileCount),
                                LatestUploadedAt = uploaderGroup.Max(summary => summary.LatestUploadedAt)
                            };
                        })
                        .OrderByDescending(summary => summary.LatestUploadedAt)
                        .ThenBy(summary => summary.Username)
                        .ToList();

                    var latestUploader = uploadedByUsers.FirstOrDefault();

                    return new PodStatusInfo
                    {
                        UploadedAt = group
                            .SelectMany(submission => submission.Attachments)
                            .Max(attachment => attachment.UploadedAt),
                        UploadedBy = latestUploader is null ||
                            string.Equals(latestUploader.Username, "Unknown uploader", StringComparison.OrdinalIgnoreCase)
                                ? null
                                : latestUploader.Username,
                        Count = group.Sum(submission => submission.Attachments.Count),
                        UploadedByUsers = uploadedByUsers
                    };
                });

        return cratePodData;
    }

    private static PodStatusInfo? MergePodStatusInfo(PodStatusInfo? invoicePodInfo, PodStatusInfo? cratePodInfo)
    {
        if (invoicePodInfo is null)
        {
            return cratePodInfo;
        }

        if (cratePodInfo is null)
        {
            return invoicePodInfo;
        }

        var uploadedByUsers = invoicePodInfo.UploadedByUsers
            .Concat(cratePodInfo.UploadedByUsers)
            .GroupBy(
                summary => string.IsNullOrWhiteSpace(summary.Username)
                    ? "Unknown uploader"
                    : summary.Username.Trim(),
                StringComparer.OrdinalIgnoreCase)
            .Select(uploaderGroup =>
            {
                var latestUpload = uploaderGroup
                    .Where(summary => summary.LatestUploadedAt.HasValue)
                    .OrderByDescending(summary => summary.LatestUploadedAt)
                    .FirstOrDefault() ?? uploaderGroup.First();

                return new PodUploadUserSummaryInfo
                {
                    Username = uploaderGroup.Key,
                    Role = latestUpload.Role,
                    AssignedSection = latestUpload.AssignedSection,
                    FileCount = uploaderGroup.Sum(summary => summary.FileCount),
                    LatestUploadedAt = uploaderGroup.Max(summary => summary.LatestUploadedAt)
                };
            })
            .OrderByDescending(summary => summary.LatestUploadedAt)
            .ThenBy(summary => summary.Username)
            .ToList();

        var latestUploader = uploadedByUsers.FirstOrDefault();
        var latestInvoicePod = invoicePodInfo.UploadedAt >= cratePodInfo.UploadedAt
            ? invoicePodInfo
            : cratePodInfo;

        return new PodStatusInfo
        {
            UploadedAt = invoicePodInfo.UploadedAt >= cratePodInfo.UploadedAt
                ? invoicePodInfo.UploadedAt
                : cratePodInfo.UploadedAt,
            UploadedBy = latestUploader?.Username is null ||
                string.Equals(latestUploader.Username, "Unknown uploader", StringComparison.OrdinalIgnoreCase)
                    ? latestInvoicePod.UploadedBy
                    : latestUploader.Username,
            Count = invoicePodInfo.Count + cratePodInfo.Count,
            UploadedByUsers = uploadedByUsers
        };
    }

    private static PodUploadStatusReportDto BuildEmptyReport(GetPodUploadStatusQuery request)
        => new()
        {
            FromDate = request.FromDate.ToString("yyyy-MM-dd"),
            ToDate = request.ToDate.ToString("yyyy-MM-dd"),
            TotalInvoices = 0,
            UploadedCount = 0,
            PendingCount = 0,
            CreditNoteDataComplete = true,
            Items = []
        };

    private sealed record CreditNoteLookupResult(
        Dictionary<int, CreditNoteInfo> Lookup,
        bool IsComplete,
        string? Warning);
}
