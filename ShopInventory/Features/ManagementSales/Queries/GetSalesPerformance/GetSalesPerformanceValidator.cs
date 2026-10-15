using FluentValidation;
using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesReview;

namespace ShopInventory.Features.ManagementSales.Queries.GetSalesPerformance;

public sealed class GetSalesPerformanceValidator : AbstractValidator<GetSalesPerformanceQuery>
{
    public GetSalesPerformanceValidator()
    {
        RuleFor(x => x.CallerUserId).NotEmpty();

        RuleFor(x => x.FromDate)
            .Must((query, from) => from.Date <= query.ToDate.Date)
            .WithMessage("The period must start on or before the day it ends.");

        // The review's limit, which is the tighter of the two reports read.
        RuleFor(x => x.ToDate)
            .Must((query, to) => (to.Date - query.FromDate.Date).TotalDays < GetDesktopSalesReviewValidator.MaxDays)
            .WithMessage($"At most {GetDesktopSalesReviewValidator.MaxDays} days can be read at once.");
    }
}
