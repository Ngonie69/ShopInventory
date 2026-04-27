using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Auth.Commands.CompleteMobileBiometricLogin;

public sealed record CompleteMobileBiometricLoginCommand(
    string RefreshToken,
    string IpAddress,
    string? DeviceId,
    string? DeviceName,
    string? BiometricCapability
) : IRequest<ErrorOr<AuthLoginResponse>>;