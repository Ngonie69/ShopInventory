using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.FactoryTransfers.Queries.GetFactoryTransfer;

public sealed class GetFactoryTransferHandler(
    ApplicationDbContext context,
    IUserManagementService userManagement)
    : IRequestHandler<GetFactoryTransferQuery, ErrorOr<FactoryTransferDetailDto>>
{
    public async Task<ErrorOr<FactoryTransferDetailDto>> Handle(
        GetFactoryTransferQuery query,
        CancellationToken cancellationToken)
    {
        var actor = await FactoryTransferActor.ResolveAsync(context, userManagement, query.UserId, cancellationToken);
        if (actor is null)
            return Errors.FactoryTransfer.UserNotFound;

        // A transfer outside the caller's warehouses reads as not found rather than forbidden, so
        // its existence is not disclosed either.
        var detail = await FactoryTransferReader.ReadDetailAsync(context, query.TransferId, actor, cancellationToken);
        return detail is null ? Errors.FactoryTransfer.NotFound(query.TransferId) : detail;
    }
}
