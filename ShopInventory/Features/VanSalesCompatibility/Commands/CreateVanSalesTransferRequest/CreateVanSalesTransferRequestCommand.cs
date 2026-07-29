using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.VanSalesCompatibility.Commands.CreateVanSalesTransferRequest;

public sealed record CreateVanSalesTransferRequestCommand(
    VanSalesTransferRequest Request,
    Guid UserId
) : IRequest<ErrorOr<VanSalesTransferRequestResponse>>;