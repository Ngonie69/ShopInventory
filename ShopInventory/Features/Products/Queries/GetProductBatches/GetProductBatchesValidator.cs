using FluentValidation;

namespace ShopInventory.Features.Products.Queries.GetProductBatches;

public sealed class GetProductBatchesValidator : AbstractValidator<GetProductBatchesQuery>
{
    public GetProductBatchesValidator()
    {
        RuleFor(x => x.WarehouseCode)
            .NotEmpty().WithMessage("Warehouse code is required");

        RuleFor(x => x.ItemCode)
            .NotEmpty().WithMessage("Item code is required");
    }
}
