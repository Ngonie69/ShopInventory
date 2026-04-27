using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.UserManagement.Queries.GetManagedMerchandiserAccounts;

public sealed record GetManagedMerchandiserAccountsQuery()
    : IRequest<ErrorOr<List<ManagedMerchandiserAccountDto>>>;