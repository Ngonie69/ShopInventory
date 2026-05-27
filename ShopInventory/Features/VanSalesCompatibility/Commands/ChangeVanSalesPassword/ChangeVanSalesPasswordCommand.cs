using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.VanSalesCompatibility.Commands.ChangeVanSalesPassword;

public sealed record ChangeVanSalesPasswordCommand(
    VanSalesPasswordChangeRequest Request,
    Guid UserId) : IRequest<ErrorOr<string>>;