using FluentValidation;

namespace ShopInventory.Features.Auth.Commands.RecordMobileBiometricPreference;

public sealed class RecordMobileBiometricPreferenceValidator : AbstractValidator<RecordMobileBiometricPreferenceCommand>
{
    public RecordMobileBiometricPreferenceValidator()
    {
        RuleFor(x => x.DeviceId).MaximumLength(128);
        RuleFor(x => x.DeviceName).MaximumLength(128);
        RuleFor(x => x.BiometricCapability).MaximumLength(64);
    }
}