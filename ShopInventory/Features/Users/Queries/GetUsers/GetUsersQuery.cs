using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Users.Queries.GetUsers;

public sealed record GetUsersQuery(
    int Page = 1,
    int PageSize = 20,
    string? Search = null,
    string? Role = null
) : IRequest<ErrorOr<UserListResponseDto>>;
