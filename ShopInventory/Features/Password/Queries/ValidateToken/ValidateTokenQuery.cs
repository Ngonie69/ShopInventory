using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Password.Queries.ValidateToken;

public sealed record ValidateTokenQuery(
    string Token
) : IRequest<ErrorOr<bool>>;
