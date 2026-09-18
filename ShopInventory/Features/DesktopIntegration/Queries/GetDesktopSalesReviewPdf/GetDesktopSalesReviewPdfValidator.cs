using FluentValidation;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesReviewPdf;

/// <remarks>The period itself is checked by the review this renders.</remarks>
public sealed class GetDesktopSalesReviewPdfValidator : AbstractValidator<GetDesktopSalesReviewPdfQuery>
{
    public GetDesktopSalesReviewPdfValidator()
    {
        RuleFor(x => x.CallerUserId).NotEmpty();
        RuleFor(x => x.Title).MaximumLength(80);
    }
}
