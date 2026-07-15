using System.Globalization;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Crates;
using ShopInventory.Common.Mobile;
using ShopInventory.Common.Pods;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.Invoices.Queries.GetPodUploadStatus;

public sealed class GetPodUploadStatusHandler(
    ISAPServiceLayerClient sapClient,
    IDocumentService documentService,
    ApplicationDbContext context,
    IOptions<SAPSettings> settings,
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
        if (!settings.Value.Enabled)
            return Errors.Invoice.SapDisabled;

        if (request.FromDate > request.ToDate)
            return Errors.Invoice.InvalidDateRange;

        try
        {
            var currentUser = request.UserId.HasValue
                ? await context.Users
                    .AsNoTracking()
                    .FirstOrDefaultAsync(u => u.Id == request.UserId.Value, cancellationToken)
                : null;

            if (request.UserId.HasValue && currentUser is null)
                return Errors.Auth.UserNotFound;

            var isPodOperator = string.Equals(currentUser?.Role, "PodOperator", StringComparison.OrdinalIgnoreCase);
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

            var invoices = await sapClient.GetInvoiceHeadersByDateRangeAsync(
                request.FromDate,
                request.ToDate,
                PodExclusions.ExcludedCardCodes.ToList(),
                includeDocumentLines: isPodOperator,
                cancellationToken);
            CreditNoteActivityInvoiceLinks? creditNoteActivity = null;
            if (request.IncludeCreditNoteActivity)
            {
                creditNoteActivity = await GetCreditNoteActivityInvoiceLinksAsync(
                    request.FromDate,
                    request.ToDate,
                    cancellationToken);
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

            if (isPodOperator && currentUser is not null)
            {
                invoices = await FilterInvoicesForPodOperatorAsync(
                    invoices,
                    currentUser.AssignedSection,
                    currentUser.Username,
                    cancellationToken);
            }

            var creditNoteLookup = await GetCreditNoteLookupWithTimeoutAsync(
                invoices,
                request.FromDate,
                request.ToDate,
                creditNoteActivity?.CreditNoteLines ?? [],
                cancellationToken);

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

            return new PodUploadStatusReportDto
            {
                FromDate = request.FromDate.ToString("yyyy-MM-dd"),
                ToDate = request.ToDate.ToString("yyyy-MM-dd"),
                TotalInvoices = items.Count,
                UploadedCount = items.Count(i => i.HasPod),
                PendingCount = items.Count(i => !i.HasPod),
                Items = items
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            return Errors.Invoice.SapTimeout;
        }
        catch (HttpRequestException ex)
        {
            return Errors.Invoice.SapConnectionError(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating POD upload status report");
            return Errors.Invoice.CreationFailed(ex.Message);
        }
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

    private async Task<CreditNoteActivityInvoiceLinks> GetCreditNoteActivityInvoiceLinksAsync(
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken)
    {
        var fromDateText = fromDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var toDateText = toDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

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
  AND T1.""CANCELED"" = 'N'
  AND T1.""DocDate"" >= '{fromDateText}'
  AND T1.""DocDate"" <= '{toDateText}'
ORDER BY T0.""BaseEntry"", T0.""BaseRef"", T1.""DocDate"", T1.""DocNum"", T0.""LineNum""";

        var rows = await sapClient.ExecuteRawSqlQueryAsync(
            $"PODCNDT{Random.Shared.Next(100000, 999999)}",
            "POD credit note activity invoice links",
            sqlText,
            cancellationToken);

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
            var chunkIndex = 0;

            foreach (var chunk in reportInvoices.Chunk(200))
            {
                chunkIndex++;
                var docEntryFilter = string.Join(", ", chunk.Select(invoice => invoice.DocEntry));
                var docNumFilter = string.Join(", ", chunk
                    .Where(invoice => invoice.DocNum > 0)
                    .Select(invoice => $"'{invoice.DocNum.ToString(CultureInfo.InvariantCulture)}'"));
                var linkFilter = string.IsNullOrWhiteSpace(docNumFilter)
                    ? $@"T0.""BaseEntry"" IN ({docEntryFilter})"
                    : $@"(T0.""BaseEntry"" IN ({docEntryFilter}) OR T0.""BaseRef"" IN ({docNumFilter}))";

                var sqlText = $@"
SELECT
    T0.""BaseEntry"" AS ""InvoiceDocEntry"",
    T0.""BaseRef"" AS ""InvoiceDocNum"",
    T1.""DocEntry"" AS ""CreditNoteDocEntry"",
    T1.""DocNum"" AS ""CreditNoteDocNum"",
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

                var rows = await sapClient.ExecuteRawSqlQueryAsync(
                    $"PODCN{Random.Shared.Next(100000, 999999)}{chunkIndex:D2}",
                    $"POD credit note links {chunkIndex}",
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

    private async Task<Dictionary<int, CreditNoteInfo>> GetCreditNoteLookupWithTimeoutAsync(
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

            return lookup;
        }
        catch (OperationCanceledException) when (!requestCancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "POD report full credit-note enrichment exceeded {TimeoutSeconds} seconds; continuing with {CreditNoteCount} credit-note entries from the date-range query",
                CreditNoteEnrichmentTimeout.TotalSeconds,
                dateRangeLookup.Count);
            return dateRangeLookup;
        }
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

        var chunkIndex = 0;
        try
        {
            foreach (var chunk in unresolvedDocEntries.Chunk(100))
            {
                chunkIndex++;
                var rows = await sapClient.ExecuteRawSqlQueryAsync(
                    $"PODCRA{Random.Shared.Next(100000, 999999)}{chunkIndex:D2}",
                    $"POD SAP crate invoice classification {chunkIndex}",
                    BuildCrateInvoiceClassificationSql(chunk),
                    cancellationToken);

                foreach (var row in rows)
                {
                    var invoiceDocEntry = TryGetInt32(row, "InvoiceDocEntry");
                    if (invoiceDocEntry.HasValue)
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
                "SAP SQL crate-invoice classification failed after {CompletedChunkCount} chunk(s); falling back to the Invoices API for {InvoiceCount} unresolved invoices",
                chunkIndex,
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

    private static string BuildCrateInvoiceClassificationSql(IEnumerable<int> invoiceDocEntries)
    {
        var docEntryFilter = string.Join(", ", invoiceDocEntries
            .Where(docEntry => docEntry > 0)
            .Distinct()
            .OrderBy(docEntry => docEntry));
        var itemCodeFilter = string.Join(", ", CrateInvoiceItemCodes
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .Select(code => $"'{code}'"));

        return $@"
SELECT DISTINCT
    T0.""DocEntry"" AS ""InvoiceDocEntry""
FROM INV1 T0
WHERE T0.""DocEntry"" IN ({docEntryFilter})
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
            Items = []
        };

    private async Task<List<Invoice>> FilterInvoicesForPodOperatorAsync(
        List<Invoice> invoices,
        string? assignedSection,
        string username,
        CancellationToken cancellationToken)
    {
        if (invoices.Count == 0)
        {
            return invoices;
        }

        if (string.IsNullOrWhiteSpace(assignedSection))
        {
            logger.LogWarning("PodOperator {Username} has no assigned section; returning no POD report items", username);
            return [];
        }

        var normalizedSection = assignedSection.Trim();
        var warehouseLocations = PodLocationScope.BuildWarehouseLocationLookup(
            await sapClient.GetWarehousesAsync(cancellationToken));
        var candidateDocEntries = invoices
            .Select(invoice => invoice.DocEntry)
            .Distinct()
            .ToList();
        var locallyScopedDocEntries = await GetLocalScopedInvoiceDocEntriesAsync(
            candidateDocEntries,
            normalizedSection,
            warehouseLocations,
            cancellationToken);

        var invoicesWithLines = invoices
            .Where(invoice => invoice.DocumentLines is { Count: > 0 })
            .ToList();

        var scopedDocEntries = invoicesWithLines
            .Where(invoice => PodLocationScope.InvoiceMatchesAssignedSection(invoice, normalizedSection, warehouseLocations))
            .Select(invoice => invoice.DocEntry)
            .ToHashSet();

        foreach (var docEntry in locallyScopedDocEntries)
        {
            scopedDocEntries.Add(docEntry);
        }

        foreach (var invoice in invoices)
        {
            var creatorLocation = PodInvoiceCreatorLocations.GetCreatorLocation(invoice.UserSign)?.Location;
            if (string.Equals(
                    PodLocationScope.CanonicalizeSection(creatorLocation),
                    PodLocationScope.CanonicalizeSection(normalizedSection),
                    StringComparison.OrdinalIgnoreCase))
            {
                scopedDocEntries.Add(invoice.DocEntry);
            }
        }

        var invoicesWithoutLines = invoices
            .Where(invoice => !scopedDocEntries.Contains(invoice.DocEntry))
            .Where(invoice => invoice.DocumentLines is null || invoice.DocumentLines.Count == 0)
            .Select(invoice => invoice.DocEntry)
            .ToList();

        if (invoicesWithoutLines.Count > 0)
        {
            var fallbackDocEntries = await documentService.GetScopedPodInvoiceDocEntriesAsync(
                invoicesWithoutLines,
                normalizedSection,
                cancellationToken);

            foreach (var docEntry in fallbackDocEntries)
            {
                scopedDocEntries.Add(docEntry);
            }
        }

        if (scopedDocEntries.Count == 0)
        {
            return [];
        }

        return invoices
            .Where(invoice => scopedDocEntries.Contains(invoice.DocEntry))
            .ToList();
    }

    private async Task<HashSet<int>> GetLocalScopedInvoiceDocEntriesAsync(
        List<int> docEntries,
        string assignedSection,
        IReadOnlyDictionary<string, string?> warehouseLocations,
        CancellationToken cancellationToken)
    {
        if (docEntries.Count == 0)
        {
            return [];
        }

        var localInvoiceWarehouseRows = await context.Invoices
            .AsNoTracking()
            .Where(invoice => invoice.SAPDocEntry.HasValue && docEntries.Contains(invoice.SAPDocEntry.Value))
            .SelectMany(invoice => invoice.DocumentLines
                .Where(line => line.WarehouseCode != null && line.WarehouseCode != string.Empty)
                .Select(line => new
                {
                    DocEntry = invoice.SAPDocEntry!.Value,
                    line.WarehouseCode
                }))
            .ToListAsync(cancellationToken);

        return localInvoiceWarehouseRows
            .GroupBy(row => row.DocEntry)
            .Where(group => PodLocationScope.WarehouseCodesMatchAssignedSection(
                group.Select(row => row.WarehouseCode),
                assignedSection,
                warehouseLocations))
            .Select(group => group.Key)
            .ToHashSet();
    }
}
