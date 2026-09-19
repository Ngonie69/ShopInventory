using FluentValidation;

namespace ShopInventory.Features.MarketBreakages.Queries.GetMyMarketBreakages;

public sealed class GetMyMarketBreakagesValidator : AbstractValidator<GetMyMarketBreakagesQuery>
{
    public GetMyMarketBreakagesValidator()
    {
        RuleFor(query => query.UserId).NotEmpty();
        RuleFor(query => query.Days).InclusiveBetween(1, 90);
    }
}
