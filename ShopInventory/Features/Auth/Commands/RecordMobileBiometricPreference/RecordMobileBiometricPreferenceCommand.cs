using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Auth.Commands.RecordMobileBiometricPreference;

public sealed record RecordMobileBiometricPreferenceCommand(
    bool Enabled,
    string? DeviceId,
    string? DeviceName,
    string? BiometricCapability
) : IRequest<ErrorOr<Success>>;