using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Pods;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Services;
using System.Globalization;

namespace ShopInventory.Features.Invoices.Queries.ValidateBulkPods;

public sealed class ValidateBulkPodsHandler(
    ApplicationDbContext context,
    ISAPServiceLayerClient sapClient,
    IDocumentService documentService,
    IOptions<SAPSettings> settings,
    ILogger<ValidateBulkPodsHandler> logger
) : IRequestHandler<ValidateBulkPodsQuery, ErrorOr<BulkPodValidationResponseDto>>
{
    public async Task<ErrorOr<BulkPodValidationResponseDto>> Handle(
        ValidateBulkPodsQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.Invoice.SapDisabled;

        var invoiceResults = await ValidateInvoiceDocNumsAsync(request.DocNums, cancellationToken);
        var salesOrderResults = await ValidateSalesOrderDocNumsAsync(request.SalesOrderDocNums, cancellationToken);

        return new BulkPodValidationResponseDto
        {
            Results = invoiceResults.Concat(salesOrderResults).ToList()
        };
    }

    private async Task<List<BulkPodValidationResultDto>> ValidateInvoiceDocNumsAsync(
        IReadOnlyList<int> requestedDocNumsInput,
        CancellationToken cancellationToken)
    {
        var requestedDocNums = requestedDocNumsInput
            .Where(docNum => docNum > 0)
            .Distinct()
            .ToList();

        if (requestedDocNums.Count == 0)
            return [];

        var cachedRows = await context.Invoices
            .AsNoTracking()
            .Where(invoice =>
                invoice.SAPDocNum.HasValue &&
                invoice.SAPDocEntry.HasValue &&
                requestedDocNums.Contains(invoice.SAPDocNum.Value))
            .Select(invoice => new BulkPodValidationResultDto
            {
                DocNum = invoice.SAPDocNum!.Value,
                DocEntry = invoice.SAPDocEntry,
                ResolvedInvoiceDocNum = invoice.SAPDocNum,
                ResolvedInvoiceDocEntry = invoice.SAPDocEntry,
                LinkedInvoiceCount = 1,
                CardCode = invoice.CardCode,
                CardName = invoice.CardName,
                Found = true
            })
            .ToListAsync(cancellationToken);

        var resultsByDocNum = cachedRows
            .GroupBy(invoice => invoice.DocNum)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(invoice => invoice.DocEntry.HasValue)
                    .ThenByDescending(invoice => !string.IsNullOrWhiteSpace(invoice.CardCode))
                    .ThenByDescending(invoice => !string.IsNullOrWhiteSpace(invoice.CardName))
                    .First());

        var missingDocNums = requestedDocNums
            .Where(docNum => !resultsByDocNum.ContainsKey(docNum))
            .ToList();

        var lookupFailures = new Dictionary<int, string>();

        if (missingDocNums.Count > 0)
        {
            try
            {
                var sapInvoices = await sapClient.GetInvoicesByDocNumsAsync(missingDocNums, cancellationToken);
                foreach (var invoice in sapInvoices
                    .Where(invoice => invoice.DocNum > 0)
                    .GroupBy(invoice => invoice.DocNum)
                    .Select(group => group.First()))
                {
                    resultsByDocNum[invoice.DocNum] = new BulkPodValidationResultDto
                    {
                        DocNum = invoice.DocNum,
                        DocEntry = invoice.DocEntry,
                        ResolvedInvoiceDocNum = invoice.DocNum,
                        ResolvedInvoiceDocEntry = invoice.DocEntry,
                        LinkedInvoiceCount = 1,
                        CardCode = invoice.CardCode,
                        CardName = invoice.CardName,
                        Found = true
                    };
                }
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Timed out while bulk-validating {Count} POD invoice numbers", missingDocNums.Count);
                foreach (var docNum in missingDocNums)
                    lookupFailures[docNum] = $"Invoice #{docNum} lookup failed (SAP timeout)";
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "Network error while bulk-validating {Count} POD invoice numbers", missingDocNums.Count);
                foreach (var docNum in missingDocNums)
                    lookupFailures[docNum] = $"Invoice #{docNum} lookup failed (SAP connection error)";
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Unexpected error while bulk-validating {Count} POD invoice numbers", missingDocNums.Count);
                foreach (var docNum in missingDocNums)
                    lookupFailures[docNum] = $"Invoice #{docNum} lookup failed";
            }
        }

        var docEntries = resultsByDocNum.Values
            .Where(result => result.Found && result.DocEntry.HasValue)
            .Select(result => result.DocEntry!.Value)
            .Distinct()
            .ToList();

        var podStatusByDocEntry = await documentService.GetPodStatusByDocEntriesAsync(docEntries, cancellationToken);

        foreach (var result in resultsByDocNum.Values)
        {
            if (result.Found && PodExclusions.IsExcludedCardCode(result.CardCode))
            {
                result.Found = false;
                result.ErrorMessage = $"Excluded BP ({result.CardCode})";
                continue;
            }

            if (result.DocEntry.HasValue && podStatusByDocEntry.TryGetValue(result.DocEntry.Value, out var podStatus))
                result.ExistingPodCount = podStatus.Count;
        }

        var results = requestedDocNums.Select(docNum =>
        {
            if (resultsByDocNum.TryGetValue(docNum, out var result))
                return result;

            return new BulkPodValidationResultDto
            {
                DocNum = docNum,
                Found = false,
                ErrorMessage = lookupFailures.TryGetValue(docNum, out var errorMessage)
                    ? errorMessage
                    : $"Invoice #{docNum} not found in SAP"
            };
        }).ToList();

        return results;
    }

    private async Task<List<BulkPodValidationResultDto>> ValidateSalesOrderDocNumsAsync(
        IReadOnlyList<int> requestedSalesOrderDocNumsInput,
        CancellationToken cancellationToken)
    {
        var requestedSalesOrderDocNums = requestedSalesOrderDocNumsInput
            .Where(docNum => docNum > 0)
            .Distinct()
            .ToList();

        if (requestedSalesOrderDocNums.Count == 0)
            return [];

        var linkedInvoices = new List<(int SalesOrderDocEntry, int SalesOrderDocNum, string? CustomerCode, string? CustomerName, int InvoiceDocEntry, int InvoiceDocNum, DateTime? InvoiceDocDate)>();
        var lookupFailures = new Dictionary<int, string>();
        var chunkIndex = 0;

        foreach (var chunk in requestedSalesOrderDocNums.Chunk(200))
        {
            chunkIndex++;

            try
            {
                var sqlText = $@"
SELECT DISTINCT
    so.""DocEntry"" AS ""SalesOrderDocEntry"",
    so.""DocNum"" AS ""SalesOrderDocNum"",
    so.""CardCode"" AS ""CustomerCode"",
    so.""CardName"" AS ""CustomerName"",
    inv.""DocEntry"" AS ""InvoiceDocEntry"",
    inv.""DocNum"" AS ""InvoiceDocNum"",
    inv.""DocDate"" AS ""InvoiceDocDate""
FROM ORDR so
INNER JOIN INV1 invl
        ON invl.""BaseType"" = 17
       AND invl.""BaseEntry"" = so.""DocEntry""
INNER JOIN OINV inv
        ON inv.""DocEntry"" = invl.""DocEntry""
WHERE so.""DocNum"" IN ({string.Join(", ", chunk)})
ORDER BY so.""DocNum"", inv.""DocDate"", inv.""DocNum""";

                var rows = await sapClient.ExecuteRawSqlQueryAsync(
                    $"POD_SO_{chunkIndex:D2}",
                    $"Sales order POD links {chunkIndex}",
                    sqlText,
                    cancellationToken);

                foreach (var row in rows)
                {
                    var salesOrderDocEntry = TryGetInt32(row, "SalesOrderDocEntry");
                    var salesOrderDocNum = TryGetInt32(row, "SalesOrderDocNum");
                    var invoiceDocEntry = TryGetInt32(row, "InvoiceDocEntry");
                    var invoiceDocNum = TryGetInt32(row, "InvoiceDocNum");

                    if (!salesOrderDocEntry.HasValue || !salesOrderDocNum.HasValue || !invoiceDocEntry.HasValue || !invoiceDocNum.HasValue)
                        continue;

                    linkedInvoices.Add((
                        salesOrderDocEntry.Value,
                        salesOrderDocNum.Value,
                        TryGetString(row, "CustomerCode"),
                        TryGetString(row, "CustomerName"),
                        invoiceDocEntry.Value,
                        invoiceDocNum.Value,
                        TryGetDateTime(row, "InvoiceDocDate")));
                }
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Timed out while resolving invoice links for {Count} sales orders", chunk.Length);
                foreach (var docNum in chunk)
                    lookupFailures[docNum] = $"Sales order #{docNum} invoice lookup failed (SAP timeout)";
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "Network error while resolving invoice links for {Count} sales orders", chunk.Length);
                foreach (var docNum in chunk)
                    lookupFailures[docNum] = $"Sales order #{docNum} invoice lookup failed (SAP connection error)";
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Unexpected error while resolving invoice links for {Count} sales orders", chunk.Length);
                foreach (var docNum in chunk)
                    lookupFailures[docNum] = $"Sales order #{docNum} invoice lookup failed";
            }
        }

        var linksBySalesOrderDocNum = linkedInvoices
            .GroupBy(link => link.SalesOrderDocNum)
            .ToDictionary(group => group.Key, group => group.ToList());

        var linkedInvoiceDocEntries = linkedInvoices
            .Select(link => link.InvoiceDocEntry)
            .Distinct()
            .ToList();

        var podStatusByDocEntry = await documentService.GetPodStatusByDocEntriesAsync(linkedInvoiceDocEntries, cancellationToken);

        return requestedSalesOrderDocNums.Select(salesOrderDocNum =>
        {
            if (!linksBySalesOrderDocNum.TryGetValue(salesOrderDocNum, out var links) || links.Count == 0)
            {
                return new BulkPodValidationResultDto
                {
                    SalesOrderDocNum = salesOrderDocNum,
                    Found = false,
                    ErrorMessage = lookupFailures.TryGetValue(salesOrderDocNum, out var errorMessage)
                        ? errorMessage
                        : $"No invoices found for sales order #{salesOrderDocNum}"
                };
            }

            var firstLink = links[0];
            if (PodExclusions.IsExcludedCardCode(firstLink.CustomerCode))
            {
                return new BulkPodValidationResultDto
                {
                    SalesOrderDocNum = salesOrderDocNum,
                    SalesOrderDocEntry = firstLink.SalesOrderDocEntry,
                    CardCode = firstLink.CustomerCode,
                    CardName = firstLink.CustomerName,
                    LinkedInvoiceCount = links.Count,
                    Found = false,
                    ErrorMessage = $"Excluded BP ({firstLink.CustomerCode})"
                };
            }

            var resolvedInvoice = links
                .Select(link => new
                {
                    Link = link,
                    PodStatus = podStatusByDocEntry.TryGetValue(link.InvoiceDocEntry, out var podStatus)
                        ? podStatus
                        : null
                })
                .OrderByDescending(item => item.PodStatus?.Count > 0)
                .ThenByDescending(item => item.PodStatus?.UploadedAt ?? DateTime.MinValue)
                .ThenByDescending(item => item.Link.InvoiceDocDate ?? DateTime.MinValue)
                .ThenByDescending(item => item.Link.InvoiceDocNum)
                .First()
                .Link;

            var totalPodCount = links.Sum(link =>
                podStatusByDocEntry.TryGetValue(link.InvoiceDocEntry, out var podStatus)
                    ? podStatus.Count
                    : 0);

            return new BulkPodValidationResultDto
            {
                DocNum = resolvedInvoice.InvoiceDocNum,
                DocEntry = resolvedInvoice.InvoiceDocEntry,
                SalesOrderDocNum = salesOrderDocNum,
                SalesOrderDocEntry = resolvedInvoice.SalesOrderDocEntry,
                ResolvedInvoiceDocNum = resolvedInvoice.InvoiceDocNum,
                ResolvedInvoiceDocEntry = resolvedInvoice.InvoiceDocEntry,
                LinkedInvoiceCount = links.Count,
                CardCode = firstLink.CustomerCode,
                CardName = firstLink.CustomerName,
                Found = true,
                ExistingPodCount = totalPodCount
            };
        }).ToList();
    }

    private static int? TryGetInt32(IReadOnlyDictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            int intValue => intValue,
            long longValue when longValue >= int.MinValue && longValue <= int.MaxValue => (int)longValue,
            decimal decimalValue when decimalValue >= int.MinValue && decimalValue <= int.MaxValue => (int)decimalValue,
            string stringValue when int.TryParse(stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private static string? TryGetString(IReadOnlyDictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var value) || value is null)
            return null;

        return value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static DateTime? TryGetDateTime(IReadOnlyDictionary<string, object?> row, string key)
    {
        var value = TryGetString(row, key);
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)
            ? parsed
            : null;
    }
}