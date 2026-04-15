using FluentValidation;

namespace ShopInventory.Features.Products.Queries.GetProductByCode;

public sealed class GetProductByCodeValidator : AbstractValidator<GetProductByCodeQuery>
{
    public GetProductByCodeValidator()
    {
        RuleFor(x => x.ItemCode)
            .NotEmpty().WithMessage("Item code is required");
    }
}
