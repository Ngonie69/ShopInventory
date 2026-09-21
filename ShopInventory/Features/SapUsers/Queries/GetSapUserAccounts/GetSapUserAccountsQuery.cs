using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.SapUsers.Queries.GetSapUserAccounts;

/// <summary>The company's SAP user accounts, optionally narrowed to a search or to the locked ones.</summary>
public sealed record GetSapUserAccountsQuery(
    string? Search,
    bool LockedOnly
) : IRequest<ErrorOr<SapUserAccountListResponseDto>>;
