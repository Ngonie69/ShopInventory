using FluentValidation;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetManagementSalesReport;

public sealed class GetManagementSalesReportValidator : AbstractValidator<GetManagementSalesReportQuery>
{
    /// <summary>The longest period one request may cover, in days.</summary>
    /// <remarks>
    /// A quarter-and-a-bit rather than the analysis page's year. Every figure is read twice — for the
    /// period and the one before it — and the margin reads SAP's invoice lines for the period, which is
    /// the slow part.
    /// </remarks>
    public const int MaxDays = 186;

    public GetManagementSalesReportValidator()
    {
        RuleFor(x => x.CallerUserId).NotEmpty();

        RuleFor(x => x.FromDate)
            .Must((query, from) => from!.Value.Date <= query.ToDate!.Value.Date)
            .When(x => x.FromDate.HasValue && x.ToDate.HasValue)
            .WithMessage("The period must start on or before the day it ends.");

        RuleFor(x => x.ToDate)
            .Must((query, to) => ((to ?? DateTime.UtcNow).Date - query.FromDate!.Value.Date).TotalDays < MaxDays)
            .When(x => x.FromDate.HasValue)
            .WithMessage($"At most {MaxDays} days can be reported on at once.");
    }
}
