using FluentValidation;

namespace ShopInventory.Features.Shops.Commands.UpdateShop;

public sealed class UpdateShopValidator : AbstractValidator<UpdateShopCommand>
{
    public UpdateShopValidator()
    {
        RuleFor(x => x.ShopId)
            .GreaterThan(0).WithMessage("A shop id is required.");

        RuleFor(x => x.Request.Name)
            .NotEmpty().WithMessage("A shop name is required.")
            .MaximumLength(100).WithMessage("Shop name must be 100 characters or fewer.");

        RuleFor(x => x.Request.BusinessPartnerCode)
            .NotEmpty().WithMessage("A business partner is required.")
            .MaximumLength(100).WithMessage("Business partner code must be 100 characters or fewer.");

        RuleFor(x => x.Request.WarehouseCode)
            .NotEmpty().WithMessage("A warehouse is required.")
            .MaximumLength(50).WithMessage("Warehouse code must be 50 characters or fewer.");

        RuleFor(x => x.Request.CostCentreCode)
            .MaximumLength(50).WithMessage("Cost centre code must be 50 characters or fewer.");
    }
}
