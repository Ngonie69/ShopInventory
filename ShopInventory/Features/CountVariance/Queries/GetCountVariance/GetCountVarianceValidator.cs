using FluentValidation;

namespace ShopInventory.Features.CountVariance.Queries.GetCountVariance;

public sealed class GetCountVarianceValidator : AbstractValidator<GetCountVarianceQuery>
{
    public GetCountVarianceValidator()
    {
        RuleFor(query => query.DocumentEntry).GreaterThan(0);
    }
}
