using FluentValidation;

namespace ShopInventory.Features.VanSalesCustomerAuth.Commands.RequestVanSalesCustomerOtp;

public sealed class RequestVanSalesCustomerOtpValidator : AbstractValidator<RequestVanSalesCustomerOtpCommand>
{
    public RequestVanSalesCustomerOtpValidator()
    {
        // Shape only. Whether the number belongs to anyone is decided in the handler, silently.
        RuleFor(x => x.PhoneNumber)
            .NotEmpty().WithMessage("Enter your phone number.")
            .MaximumLength(32).WithMessage("That does not look like a phone number.");
    }
}
