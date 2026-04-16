using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.UserManagement.Commands.UpdateUserPermissions;

public sealed record UpdateUserPermissionsCommand(
    Guid Id,
    UpdatePermissionsRequest Request
) : IRequest<ErrorOr<Success>>;
