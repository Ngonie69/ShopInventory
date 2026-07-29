using ErrorOr;
using MediatR;
using ShopInventory.Models;

namespace ShopInventory.Features.Users.Queries.GetRoles;

public sealed class GetRolesHandler
    : IRequestHandler<GetRolesQuery, ErrorOr<IReadOnlyList<string>>>
{
    public Task<ErrorOr<IReadOnlyList<string>>> Handle(
        GetRolesQuery query,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> roles = ApplicationRoles.AssignableRoles;
        ErrorOr<IReadOnlyList<string>> result = ErrorOrFactory.From(roles);
        return Task.FromResult(result);
    }
}
