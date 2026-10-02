using FluentValidation;

namespace ShopInventory.Features.MarketBreakages.Queries.GetMarketBreakages;

public sealed class GetMarketBreakagesValidator : AbstractValidator<GetMarketBreakagesQuery>
{
    public GetMarketBreakagesValidator()
    {
        RuleFor(query => query.Page).GreaterThan(0);
        RuleFor(query => query.PageSize).InclusiveBetween(1, 200);
        RuleFor(query => query.Search).MaximumLength(100);
        RuleFor(query => query.Status)
            .Must(MarketBreakageFilters.IsKnownStatus)
            .WithMessage(MarketBreakageFilters.StatusMessage);
    }
}
