using FluentValidation;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetCounterMonthOnMonth;

public sealed class GetCounterMonthOnMonthValidator : AbstractValidator<GetCounterMonthOnMonthQuery>
{
    public GetCounterMonthOnMonthValidator()
    {
        RuleFor(x => x.CallerUserId).NotEmpty();

        RuleFor(x => x.CurrentMonth)
            .Must((query, current) => MonthIndex(query.PreviousMonth) != MonthIndex(current))
            .WithMessage("Choose two different months.");

        RuleFor(x => x.CurrentMonth)
            .Must(current => MonthIndex(current) <= MonthIndex(DateTime.UtcNow))
            .WithMessage("The month cannot be in the future.");

        RuleFor(x => x.PreviousMonth)
            .Must(previous => MonthIndex(previous) <= MonthIndex(DateTime.UtcNow))
            .WithMessage("The month cannot be in the future.");

        RuleFor(x => x.PreviousMonth.Year).InclusiveBetween(2020, 2100);
        RuleFor(x => x.CurrentMonth.Year).InclusiveBetween(2020, 2100);
    }

    private static int MonthIndex(DateTime date) => date.Year * 12 + date.Month;
}
