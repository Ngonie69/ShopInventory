using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.CountVariance.Queries.GetCountingDocuments;

public sealed class GetCountingDocumentsHandler(
    ICountVarianceService countVarianceService,
    ILogger<GetCountingDocumentsHandler> logger)
    : IRequestHandler<GetCountingDocumentsQuery, ErrorOr<List<CountingDocumentSummary>>>
{
    public async Task<ErrorOr<List<CountingDocumentSummary>>> Handle(
        GetCountingDocumentsQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var (success, message, response) =
                await countVarianceService.GetDocumentsAsync(request.Status, request.Search, cancellationToken);

            return success && response is not null
                ? response.Documents
                : Errors.CountVariance.LoadFailed(message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error loading the inventory counts");
            return Errors.CountVariance.LoadFailed("The inventory counts could not be loaded.");
        }
    }
}
