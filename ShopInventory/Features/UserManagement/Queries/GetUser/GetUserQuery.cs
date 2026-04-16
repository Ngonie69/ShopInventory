using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.UserManagement.Queries.GetUser;

public sealed record GetUserQuery(Guid Id) : IRequest<ErrorOr<UserDetailDto>>;
