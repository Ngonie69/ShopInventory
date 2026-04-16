using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetPagedTransfers;

public sealed record GetPagedTransfersQuery(
    string WarehouseCode,
    int Page = 1,
    int PageSize = 20
) : IRequest<ErrorOr<List<InventoryTransferDto>>>;
