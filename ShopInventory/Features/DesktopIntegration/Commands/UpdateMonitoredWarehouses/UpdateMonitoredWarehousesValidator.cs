using FluentValidation;

namespace ShopInventory.Features.DesktopIntegration.Commands.UpdateMonitoredWarehouses;

public sealed class UpdateMonitoredWarehousesValidator : AbstractValidator<UpdateMonitoredWarehousesCommand>
{
    public UpdateMonitoredWarehousesValidator()
    {
        RuleFor(x => x.CallerUserId).NotEmpty();

        // An empty list would snapshot nothing and leave every till refusing every sale.
        RuleFor(x => x.Warehouses)
            .Must(warehouses => warehouses.Any(code => !string.IsNullOrWhiteSpace(code)))
            .WithMessage("At least one warehouse has to be monitored.");

        RuleForEach(x => x.Warehouses)
            .MaximumLength(20)
            .WithMessage("'{PropertyValue}' is too long to be a warehouse code.");
    }
}
