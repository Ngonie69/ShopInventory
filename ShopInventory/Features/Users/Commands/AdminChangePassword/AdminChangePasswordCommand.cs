using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Users.Commands.AdminChangePassword;

public sealed record AdminChangePasswordCommand(
    Guid Id,
    AdminChangePasswordRequest Request
) : IRequest<ErrorOr<Success>>;
