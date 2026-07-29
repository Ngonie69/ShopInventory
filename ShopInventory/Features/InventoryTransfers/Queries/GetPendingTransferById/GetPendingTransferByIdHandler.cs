using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;

namespace ShopInventory.Features.InventoryTransfers.Queries.GetPendingTransferById;

public sealed class GetPendingTransferByIdHandler(
    ApplicationDbContext context,
    IPendingInventoryTransferEnricher enricher)
    : IRequestHandler<GetPendingTransferByIdQuery, ErrorOr<PendingInventoryTransferDto>>
{
    public async Task<ErrorOr<PendingInventoryTransferDto>> Handle(
        GetPendingTransferByIdQuery query,
        CancellationToken cancellationToken)
    {
        var pending = await context.PendingInventoryTransfers
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == query.PendingTransferId, cancellationToken);
        if (pending is null)
            return Errors.InventoryTransfer.PendingTransferNotFound(query.PendingTransferId);

        var enriched = await enricher.EnrichAsync([pending], query.UserId, includeLines: true, cancellationToken);
        return enriched[0];
    }
}
