using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.VanSalesCompatibility.Commands.ConfirmVanSalesTransferRequest;

public sealed record ConfirmVanSalesTransferRequestCommand(
    VanSalesTransferApprovalRequest Request,
    Guid UserId) : IRequest<ErrorOr<string>>;