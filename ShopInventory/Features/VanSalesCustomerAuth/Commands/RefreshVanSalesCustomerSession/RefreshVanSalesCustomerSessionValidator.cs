using FluentValidation;

namespace ShopInventory.Features.VanSalesCustomerAuth.Commands.RefreshVanSalesCustomerSession;

public sealed class RefreshVanSalesCustomerSessionValidator
    : AbstractValidator<RefreshVanSalesCustomerSessionCommand>
{
    public RefreshVanSalesCustomerSessionValidator()
    {
        RuleFor(x => x.RefreshToken)
            .NotEmpty().WithMessage("A refresh token is required.")
            .MaximumLength(200);

        RuleFor(x => x.DeviceId).MaximumLength(128);
        RuleFor(x => x.DeviceName).MaximumLength(200);
    }
}
