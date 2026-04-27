using FluentValidation;

namespace ShopInventory.Features.Auth.Commands.CompleteMobileBiometricLogin;

public sealed class CompleteMobileBiometricLoginValidator : AbstractValidator<CompleteMobileBiometricLoginCommand>
{
    public CompleteMobileBiometricLoginValidator()
    {
        RuleFor(x => x.RefreshToken)
            .NotEmpty()
            .MaximumLength(2048);

        RuleFor(x => x.DeviceId).MaximumLength(128);
        RuleFor(x => x.DeviceName).MaximumLength(128);
        RuleFor(x => x.BiometricCapability).MaximumLength(64);
    }
}