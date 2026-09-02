using FluentValidation;

namespace ShopInventory.Features.VanSalesCustomerAuth.Commands.OnboardVanSalesCustomerAccount;

public sealed class OnboardVanSalesCustomerAccountValidator
    : AbstractValidator<OnboardVanSalesCustomerAccountCommand>
{
    public OnboardVanSalesCustomerAccountValidator()
    {
        RuleFor(x => x.RouteCustomerId)
            .GreaterThan(0).WithMessage("Choose the customer this sign-in belongs to.");

        RuleFor(x => x.PhoneNumber)
            .NotEmpty().WithMessage("Enter the customer's phone number.")
            .MaximumLength(32);

        RuleFor(x => x.DisplayName).MaximumLength(200);

        // Checked only when one was typed. Blank is how an operator says "leave the existing
        // password alone", and whether that is allowed depends on the account already existing —
        // a question for the handler, not for a rule that has never looked at the database.
        RuleFor(x => x.Password)
            .MinimumLength(VanSalesCustomerPassword.MinimumLength)
                .WithMessage($"Use at least {VanSalesCustomerPassword.MinimumLength} characters.")
            .MaximumLength(VanSalesCustomerPassword.MaximumLength)
                .WithMessage($"Use at most {VanSalesCustomerPassword.MaximumLength} characters.")
            .When(x => !string.IsNullOrWhiteSpace(x.Password));
    }
}
