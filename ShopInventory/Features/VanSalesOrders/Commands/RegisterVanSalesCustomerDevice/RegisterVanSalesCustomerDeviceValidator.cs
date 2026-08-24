using FluentValidation;

namespace ShopInventory.Features.VanSalesOrders.Commands.RegisterVanSalesCustomerDevice;

public sealed class RegisterVanSalesCustomerDeviceValidator
    : AbstractValidator<RegisterVanSalesCustomerDeviceCommand>
{
    public RegisterVanSalesCustomerDeviceValidator()
    {
        RuleFor(x => x.DeviceToken)
            .NotEmpty().WithMessage("A device token is required.")
            .MaximumLength(512);

        RuleFor(x => x.DeviceId).MaximumLength(128);
        RuleFor(x => x.DeviceName).MaximumLength(200);
        RuleFor(x => x.AppVersion).MaximumLength(50);
    }
}
