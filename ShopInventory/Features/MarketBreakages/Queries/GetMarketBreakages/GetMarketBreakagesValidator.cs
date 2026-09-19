using FluentValidation;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.MarketBreakages.Queries.GetMarketBreakages;

public sealed class GetMarketBreakagesValidator : AbstractValidator<GetMarketBreakagesQuery>
{
    public GetMarketBreakagesValidator()
    {
        RuleFor(query => query.Page).GreaterThan(0);
        RuleFor(query => query.PageSize).InclusiveBetween(1, 200);
        RuleFor(query => query.Search).MaximumLength(100);
        RuleFor(query => query.Status)
            .Must(status => string.IsNullOrWhiteSpace(status)
                || string.Equals(status, GetMarketBreakagesHandler.OpenFilter, StringComparison.OrdinalIgnoreCase)
                || MarketBreakageStatuses.All.Contains(status, StringComparer.OrdinalIgnoreCase))
            .WithMessage($"Status must be open or one of: {string.Join(", ", MarketBreakageStatuses.All)}.");
    }
}
