using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Users.Queries.GetUser;

public sealed record GetUserQuery(Guid Id) : IRequest<ErrorOr<UserDto>>;
