using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.TwoFactor.Commands.InitiateTwoFactorSetup;

public sealed record InitiateTwoFactorSetupCommand(
    Guid UserId
) : IRequest<ErrorOr<TwoFactorSetupResponse>>;
