using ErrorOr;
using MediatR;

namespace ShopInventory.Features.UserManagement.Queries.GetCurrentUserPermissions;

public sealed record GetCurrentUserPermissionsQuery(Guid UserId) : IRequest<ErrorOr<List<string>>>;
