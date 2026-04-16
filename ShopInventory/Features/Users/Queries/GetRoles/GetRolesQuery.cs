using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Users.Queries.GetRoles;

public sealed record GetRolesQuery() : IRequest<ErrorOr<IReadOnlyList<string>>>;
