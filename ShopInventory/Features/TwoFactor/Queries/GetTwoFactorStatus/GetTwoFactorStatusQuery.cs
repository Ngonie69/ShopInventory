using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.TwoFactor.Queries.GetTwoFactorStatus;

public sealed record GetTwoFactorStatusQuery(
    Guid UserId
) : IRequest<ErrorOr<TwoFactorStatusResponse>>;
