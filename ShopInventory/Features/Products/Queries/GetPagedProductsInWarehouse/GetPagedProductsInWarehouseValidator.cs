using FluentValidation;

namespace ShopInventory.Features.Products.Queries.GetPagedProductsInWarehouse;

public sealed class GetPagedProductsInWarehouseValidator : AbstractValidator<GetPagedProductsInWarehouseQuery>
{
    public GetPagedProductsInWarehouseValidator()
    {
        RuleFor(x => x.WarehouseCode)
            .NotEmpty().WithMessage("Warehouse code is required");

        RuleFor(x => x.Page)
            .GreaterThanOrEqualTo(1).WithMessage("Page number must be at least 1");

        RuleFor(x => x.PageSize)
            .InclusiveBetween(1, 100).WithMessage("Page size must be between 1 and 100");
    }
}
