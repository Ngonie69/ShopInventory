using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.UserManagement.Queries.GetUserPermissions;

public sealed record GetUserPermissionsQuery(Guid Id) : IRequest<ErrorOr<UserPermissionsResponse>>;
