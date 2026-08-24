using FluentValidation;

namespace ShopInventory.Features.RouteCustomers.Commands.SetRouteCustomerVisitDays;

public sealed class SetRouteCustomerVisitDaysValidator
    : AbstractValidator<SetRouteCustomerVisitDaysCommand>
{
    public SetRouteCustomerVisitDaysValidator()
    {
        RuleFor(x => x.RouteCustomerId).GreaterThan(0);

        RuleFor(x => x.VisitDays)
            .NotNull().WithMessage("Supply the calling days, or an empty list to clear them.");

        // An empty list is allowed — it means "we do not know", which is a real state and the one
        // every customer starts in. What is not allowed is a value outside the week, which would
        // store a day that no date can ever match and silently never deliver.
        RuleForEach(x => x.VisitDays)
            .IsInEnum().WithMessage("That is not a day of the week.");
    }
}
