using FluentValidation;
using ShopInventory.Features.DesktopIntegration.Queries.GetManagementSalesReport;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesReview;

public sealed class GetDesktopSalesReviewValidator : AbstractValidator<GetDesktopSalesReviewQuery>
{
    /// <summary>The longest period one review may cover, in days.</summary>
    /// <remarks>The management report's limit, since the review reads it: SAP's invoice lines are the slow part.</remarks>
    public const int MaxDays = GetManagementSalesReportValidator.MaxDays;

    public GetDesktopSalesReviewValidator()
    {
        RuleFor(x => x.CallerUserId).NotEmpty();

        RuleFor(x => x.FromDate)
            .Must((query, from) => from!.Value.Date <= query.ToDate!.Value.Date)
            .When(x => x.FromDate.HasValue && x.ToDate.HasValue)
            .WithMessage("The period must start on or before the day it ends.");

        RuleFor(x => x.ToDate)
            .Must((query, to) => ((to ?? DateTime.UtcNow).Date - query.FromDate!.Value.Date).TotalDays < MaxDays)
            .When(x => x.FromDate.HasValue)
            .WithMessage($"At most {MaxDays} days can be reviewed at once.");
    }
}
