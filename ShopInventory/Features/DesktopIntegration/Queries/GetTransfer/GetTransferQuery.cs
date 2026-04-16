using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetTransfer;

public sealed record GetTransferQuery(
    int DocEntry
) : IRequest<ErrorOr<InventoryTransferDto>>;
