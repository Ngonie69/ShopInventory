using FluentValidation;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.DailyIncomingPayments.Queries.GetDailyIncomingPayments;

public sealed class GetDailyIncomingPaymentsValidator : AbstractValidator<GetDailyIncomingPaymentsQuery>
{
    public const int MaxDays = 366;

    public GetDailyIncomingPaymentsValidator()
    {
        RuleFor(x => x.To)
            .GreaterThanOrEqualTo(x => x.From)
            .WithMessage("The end date is before the start date.");

        RuleFor(x => x)
            .Must(x => (x.To.Date - x.From.Date).TotalDays < MaxDays)
            .WithMessage($"Pick a range of at most {MaxDays} days.");

        RuleFor(x => x.Status)
            .Must(status => string.IsNullOrWhiteSpace(status)
                            || Enum.TryParse<DailyIncomingPaymentStatus>(status, ignoreCase: true, out _))
            .WithMessage("Unknown status.");
    }
}
