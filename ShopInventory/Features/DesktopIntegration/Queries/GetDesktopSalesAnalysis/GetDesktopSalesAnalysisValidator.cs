using FluentValidation;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesAnalysis;

public sealed class GetDesktopSalesAnalysisValidator : AbstractValidator<GetDesktopSalesAnalysisQuery>
{
    /// <summary>The longest period one request may analyse, in days.</summary>
    /// <remarks>
    /// A year, so a year-on-year question can be asked in one go. The analysis is a handful of grouped
    /// queries rather than a row dump, but every one of them scans the period.
    /// </remarks>
    public const int MaxDays = 366;

    public GetDesktopSalesAnalysisValidator()
    {
        RuleFor(x => x.CallerUserId).NotEmpty();

        RuleFor(x => x.FromDate)
            .Must((query, from) => from!.Value.Date <= query.ToDate!.Value.Date)
            .When(x => x.FromDate.HasValue && x.ToDate.HasValue)
            .WithMessage("The period must start on or before the day it ends.");

        // Measured against today when the end is omitted, which is what the handler defaults it to.
        RuleFor(x => x.ToDate)
            .Must((query, to) => ((to ?? DateTime.UtcNow).Date - query.FromDate!.Value.Date).TotalDays < MaxDays)
            .When(x => x.FromDate.HasValue)
            .WithMessage($"At most {MaxDays} days can be analysed at once.");
    }
}
