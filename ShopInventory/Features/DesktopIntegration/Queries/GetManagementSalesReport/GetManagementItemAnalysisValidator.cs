using FluentValidation;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetManagementSalesReport;

public sealed class GetManagementItemAnalysisValidator : AbstractValidator<GetManagementItemAnalysisQuery>
{
    public GetManagementItemAnalysisValidator()
    {
        RuleFor(x => x.CallerUserId).NotEmpty();
        RuleFor(x => x.ItemCode).NotEmpty().MaximumLength(50);

        RuleFor(x => x.FromDate)
            .Must((query, from) => from!.Value.Date <= query.ToDate!.Value.Date)
            .When(x => x.FromDate.HasValue && x.ToDate.HasValue)
            .WithMessage("The period must start on or before the day it ends.");

        RuleFor(x => x.ToDate)
            .Must((query, to) => ((to ?? DateTime.UtcNow).Date - query.FromDate!.Value.Date).TotalDays < GetManagementSalesReportValidator.MaxDays)
            .When(x => x.FromDate.HasValue)
            .WithMessage($"At most {GetManagementSalesReportValidator.MaxDays} days can be reported on at once.");
    }
}
