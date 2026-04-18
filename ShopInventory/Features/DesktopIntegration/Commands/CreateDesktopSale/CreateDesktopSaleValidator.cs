using FluentValidation;

namespace ShopInventory.Features.DesktopIntegration.Commands.CreateDesktopSale;

public sealed class CreateDesktopSaleValidator : AbstractValidator<CreateDesktopSaleCommand>
{
    public CreateDesktopSaleValidator()
    {
        RuleFor(x => x.Request.CardCode).NotEmpty().WithMessage("Customer code is required");
        RuleFor(x => x.Request.WarehouseCode).NotEmpty().WithMessage("Warehouse code is required");
        RuleFor(x => x.Request.Lines).NotEmpty().WithMessage("At least one line item is required");

        RuleForEach(x => x.Request.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.ItemCode).NotEmpty();
            line.RuleFor(l => l.Quantity).GreaterThan(0);
            line.RuleFor(l => l.WarehouseCode).NotEmpty();
            line.RuleFor(l => l.UnitPrice).GreaterThanOrEqualTo(0);
            line.RuleFor(l => l.DiscountPercent).InclusiveBetween(0, 100);
        });
    }
}
