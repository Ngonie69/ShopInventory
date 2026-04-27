using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.UserManagement.Queries.GetManagedMerchandiserAccounts;

public sealed record GetManagedMerchandiserAccountsQuery()
    : IRequest<ErrorOr<GetManagedMerchandiserAccountsResult>>;