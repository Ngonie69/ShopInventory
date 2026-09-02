using FluentValidation;

namespace ShopInventory.Features.VanSalesCustomerAuth.Commands.SignInVanSalesCustomer;

/// <summary>
/// Shape only.
/// </summary>
/// <remarks>
/// No minimum length on the password here, deliberately, even though setting one is held to
/// <see cref="VanSalesCustomerPassword.MinimumLength"/>. A rule on the way in would answer "that is
/// too short to be anyone's password" without touching the database — which tells a caller probing
/// the endpoint exactly which guesses are worth making, and tells a customer whose password predates
/// the rule that theirs cannot possibly be right.
/// </remarks>
public sealed class SignInVanSalesCustomerValidator : AbstractValidator<SignInVanSalesCustomerCommand>
{
    public SignInVanSalesCustomerValidator()
    {
        RuleFor(x => x.PhoneNumber)
            .NotEmpty().WithMessage("Enter your phone number.")
            .MaximumLength(32);

        RuleFor(x => x.Password)
            .NotEmpty().WithMessage("Enter your password.")
            .MaximumLength(VanSalesCustomerPassword.MaximumLength);

        RuleFor(x => x.DeviceId).MaximumLength(128);
        RuleFor(x => x.DeviceName).MaximumLength(200);
    }
}
