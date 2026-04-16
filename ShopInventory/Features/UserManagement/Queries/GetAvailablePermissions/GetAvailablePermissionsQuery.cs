using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.UserManagement.Queries.GetAvailablePermissions;

public sealed record GetAvailablePermissionsQuery() : IRequest<ErrorOr<AvailablePermissionsResponse>>;
