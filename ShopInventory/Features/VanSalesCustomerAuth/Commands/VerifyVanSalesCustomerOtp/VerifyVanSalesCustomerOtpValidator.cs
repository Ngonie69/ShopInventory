using FluentValidation;

namespace ShopInventory.Features.VanSalesCustomerAuth.Commands.VerifyVanSalesCustomerOtp;

public sealed class VerifyVanSalesCustomerOtpValidator : AbstractValidator<VerifyVanSalesCustomerOtpCommand>
{
    public VerifyVanSalesCustomerOtpValidator()
    {
        RuleFor(x => x.PhoneNumber)
            .NotEmpty().WithMessage("Enter your phone number.")
            .MaximumLength(32);

        RuleFor(x => x.Code)
            .NotEmpty().WithMessage("Enter the code we sent you.")
            .MaximumLength(12);

        RuleFor(x => x.DeviceId).MaximumLength(128);
        RuleFor(x => x.DeviceName).MaximumLength(200);
    }
}
