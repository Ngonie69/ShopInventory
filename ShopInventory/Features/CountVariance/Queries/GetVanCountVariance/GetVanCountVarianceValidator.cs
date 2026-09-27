using FluentValidation;

namespace ShopInventory.Features.CountVariance.Queries.GetVanCountVariance;

public sealed class GetVanCountVarianceValidator : AbstractValidator<GetVanCountVarianceQuery>
{
    /// <summary>
    /// A quarter. Every count in the range is read in full to find which warehouses its lines are in,
    /// so the range is what bounds the SAP work.
    /// </summary>
    public const int MaxRangeDays = 92;

    public GetVanCountVarianceValidator()
    {
        RuleFor(query => query.ToDate)
            .GreaterThanOrEqualTo(query => query.FromDate)
            .WithMessage("The range must end on or after the day it starts.");

        RuleFor(query => query)
            .Must(query => (query.ToDate.Date - query.FromDate.Date).TotalDays < MaxRangeDays)
            .WithMessage($"Choose a range of at most {MaxRangeDays} days.");
    }
}
