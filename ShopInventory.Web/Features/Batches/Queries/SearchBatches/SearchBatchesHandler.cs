using System.Net.Http.Json;
using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.Batches.Queries.SearchBatches;

public sealed class SearchBatchesHandler(
    HttpClient httpClient,
    ILogger<SearchBatchesHandler> logger
) : IRequestHandler<SearchBatchesQuery, ErrorOr<BatchSearchResponse>>
{
    public async Task<ErrorOr<BatchSearchResponse>> Handle(
        SearchBatchesQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = $"api/batch/search?term={Uri.EscapeDataString(request.SearchTerm.Trim())}";
            var response = await httpClient.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Failed to search batches from {Url}. Status code: {StatusCode}",
                    url,
                    (int)response.StatusCode);
                return Errors.Batch.SearchFailed("Failed to search batches.");
            }

            var result = await response.Content.ReadFromJsonAsync<BatchSearchResponse>(cancellationToken: cancellationToken);
            if (result is null)
            {
                return Errors.Batch.SearchFailed("Failed to search batches.");
            }

            foreach (var item in result.Results)
            {
                item.PendingStatus = item.Status;
            }

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error searching batches in web CQRS handler");
            return Errors.Batch.SearchFailed("Failed to search batches.");
        }
    }
}