using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Batches.Queries.SearchBatches;

public sealed class SearchBatchesHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<SearchBatchesHandler> logger
) : IRequestHandler<SearchBatchesQuery, ErrorOr<BatchSearchResponseDto>>
{
    public async Task<ErrorOr<BatchSearchResponseDto>> Handle(
        SearchBatchesQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
        {
            return Errors.Batch.SapDisabled;
        }

        try
        {
            var results = await sapClient.SearchBatchesByBatchNumberAsync(request.SearchTerm, cancellationToken);

            var response = new BatchSearchResponseDto
            {
                SearchTerm = request.SearchTerm.Trim(),
                ResultCount = results.Count,
                Results = results.Select(result => new BatchSearchResultDto
                {
                    BatchEntryId = result.BatchEntryId,
                    ItemCode = result.ItemCode,
                    ItemName = result.ItemName,
                    BatchNumber = result.BatchNumber,
                    WarehouseCode = result.WarehouseCode,
                    Quantity = result.Quantity,
                    Status = result.Status,
                    ExpiryDate = result.ExpiryDate,
                    ManufacturingDate = result.ManufacturingDate,
                    AdmissionDate = result.AdmissionDate,
                    Notes = result.Notes
                }).ToList()
            };

            logger.LogInformation(
                "Found {ResultCount} batch search result(s) for term {SearchTerm}",
                response.ResultCount,
                response.SearchTerm);

            return response;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            logger.LogError(ex, "Timeout searching batches in SAP Service Layer");
            return Errors.Batch.SapTimeout;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Network error searching batches in SAP Service Layer");
            return Errors.Batch.SapConnectionError(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error searching batches for term {SearchTerm}", request.SearchTerm);
            return Errors.Batch.SearchFailed("Failed to search batches.");
        }
    }
}