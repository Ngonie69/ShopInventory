using FluentValidation;

namespace ShopInventory.Features.MarketBreakages.Queries.GetMarketBreakage;

public sealed class GetMarketBreakageValidator : AbstractValidator<GetMarketBreakageQuery>
{
    public GetMarketBreakageValidator()
    {
        RuleFor(query => query.BreakageId).GreaterThan(0);
    }
}
