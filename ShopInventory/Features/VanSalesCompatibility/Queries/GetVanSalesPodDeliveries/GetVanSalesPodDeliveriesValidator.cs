using FluentValidation;

namespace ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesPodDeliveries;

public sealed class GetVanSalesPodDeliveriesValidator : AbstractValidator<GetVanSalesPodDeliveriesQuery>
{
    /// <summary>
    /// The longest range a handset may ask for. The report is built from SAP on a cache miss, and a van
    /// asks over whatever signal the road has; a month covers any delivery still worth chasing a note for.
    /// </summary>
    public const int MaxDays = 31;

    public GetVanSalesPodDeliveriesValidator()
    {
        RuleFor(x => x.ToDate)
            .GreaterThanOrEqualTo(x => x.FromDate)
            .WithMessage("The end date must not be before the start date.");

        RuleFor(x => x)
            .Must(x => (x.ToDate.Date - x.FromDate.Date).TotalDays <= MaxDays)
            .WithName("ToDate")
            .WithMessage($"Deliveries can be listed for at most {MaxDays} days at a time.");
    }
}
