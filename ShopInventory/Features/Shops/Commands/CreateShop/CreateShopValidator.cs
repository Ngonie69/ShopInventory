using FluentValidation;

namespace ShopInventory.Features.Shops.Commands.CreateShop;

/// <summary>
/// Shape and format only. Uniqueness and the warehouse-already-taken rule are data-dependent and
/// live in the handler.
/// </summary>
public sealed class CreateShopValidator : AbstractValidator<CreateShopCommand>
{
    public CreateShopValidator()
    {
        RuleFor(x => x.Request.Code)
            .NotEmpty().WithMessage("A shop code is required.")
            .MaximumLength(30).WithMessage("Shop code must be 30 characters or fewer.")
            // Grouped on in reports and used in URLs, so it stays to characters that survive both.
            .Matches("^[A-Za-z0-9_-]+$")
            .WithMessage("Shop code may contain only letters, numbers, hyphens and underscores.");

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
