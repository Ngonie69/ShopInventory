using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.SapUserAccounts.Queries.GetSapUserAccounts;

/// <summary>Everything the SAP user administration screen renders.</summary>
public sealed record GetSapUserAccountsQuery(
    string? Search,
    bool LockedOnly
) : IRequest<ErrorOr<SapUserAccountListModel>>;
