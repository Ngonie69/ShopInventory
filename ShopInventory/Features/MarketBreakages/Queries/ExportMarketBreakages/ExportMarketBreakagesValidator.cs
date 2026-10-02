using FluentValidation;

namespace ShopInventory.Features.MarketBreakages.Queries.ExportMarketBreakages;

public sealed class ExportMarketBreakagesValidator : AbstractValidator<ExportMarketBreakagesQuery>
{
    public ExportMarketBreakagesValidator()
    {
        RuleFor(query => query.Search).MaximumLength(100);
        RuleFor(query => query.Status)
            .Must(MarketBreakageFilters.IsKnownStatus)
            .WithMessage(MarketBreakageFilters.StatusMessage);
    }
}
