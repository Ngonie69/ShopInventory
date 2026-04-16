using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Users.Queries.GetRoles;

public sealed class GetRolesHandler
    : IRequestHandler<GetRolesQuery, ErrorOr<IReadOnlyList<string>>>
{
    public Task<ErrorOr<IReadOnlyList<string>>> Handle(
        GetRolesQuery query,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> roles = new List<string> { "Admin", "Cashier", "StockController", "DepotController", "PodOperator" };
        ErrorOr<IReadOnlyList<string>> result = ErrorOrFactory.From(roles);
        return Task.FromResult(result);
    }
}
