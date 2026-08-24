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
    }
}
