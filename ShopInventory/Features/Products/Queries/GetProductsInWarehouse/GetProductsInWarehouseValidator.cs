using FluentValidation;

namespace ShopInventory.Features.Products.Queries.GetProductsInWarehouse;

public sealed class GetProductsInWarehouseValidator : AbstractValidator<GetProductsInWarehouseQuery>
{
    public GetProductsInWarehouseValidator()
    {
        RuleFor(x => x.WarehouseCode)
            .NotEmpty().WithMessage("Warehouse code is required");
    }
}
