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

        var requestedDocNums = request.DocNums
            .Where(docNum => docNum > 0)
            .Distinct()
            .ToList();

        if (requestedDocNums.Count == 0)
            return new BulkPodValidationResponseDto();

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

        return new BulkPodValidationResponseDto { Results = results };
    }
}