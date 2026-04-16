using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetTransferRequest;

public sealed record GetTransferRequestQuery(
    int DocEntry
) : IRequest<ErrorOr<InventoryTransferRequestDto>>;
