using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.CountVariance.Queries.GetCountingDocuments;

public sealed class GetCountingDocumentsHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> sapSettings,
    ILogger<GetCountingDocumentsHandler> logger)
    : IRequestHandler<GetCountingDocumentsQuery, ErrorOr<CountingDocumentListResponseDto>>
{
    /// <summary>
    /// A picker's worth. Older counts are found by number or remarks, the way B1's own Find works.
    /// </summary>
    public const int ListSize = 50;

    public async Task<ErrorOr<CountingDocumentListResponseDto>> Handle(
        GetCountingDocumentsQuery query,
        CancellationToken cancellationToken)
    {
        if (!sapSettings.Value.Enabled)
            return Errors.CountVariance.SapDisabled;

        var sapStatus = query.Status?.Trim().ToLowerInvariant() switch
        {
            "closed" => "cdsClosed",
            "all" => null,
            _ => "cdsOpen"
        };

        List<InventoryCounting> counts;
        try
        {
            counts = await sapClient.GetInventoryCountingsAsync(sapStatus, query.Search, ListSize, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Reading the SAP inventory counts failed");
            return Errors.CountVariance.SapReadFailed("list the inventory counts", ex.Message);
        }

        var counterNames = await CounterNames.ReadAsync(sapClient, counts, logger, cancellationToken);

        return new CountingDocumentListResponseDto
        {
            Documents = counts
                .Select(count => CounterNames.Summarise(count, counterNames))
                .ToList()
        };
    }
}
