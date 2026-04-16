using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.UserManagement.Queries.GetUsers;

public sealed record GetUsersQuery(
    int Page = 1,
    int PageSize = 10,
    string? Search = null,
    string? Role = null,
    bool? IsActive = null
) : IRequest<ErrorOr<PagedResult<UserDetailDto>>>;
