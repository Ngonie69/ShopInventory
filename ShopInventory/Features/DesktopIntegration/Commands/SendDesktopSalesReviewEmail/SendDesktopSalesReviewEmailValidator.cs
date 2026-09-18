using FluentValidation;
using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesReviewSchedule;

namespace ShopInventory.Features.DesktopIntegration.Commands.SendDesktopSalesReviewEmail;

public sealed class SendDesktopSalesReviewEmailValidator : AbstractValidator<SendDesktopSalesReviewEmailCommand>
{
    public SendDesktopSalesReviewEmailValidator()
    {
        RuleFor(x => x.Cadence)
            .Must(DesktopSalesReviewCadence.IsKnown)
            .WithMessage("The period must be weekly, monthly or custom.");

        RuleFor(x => x.CallerUserId)
            .NotEmpty()
            .When(x => !x.Scheduled)
            .WithMessage("A review sent by hand is read as the person sending it.");

        RuleFor(x => x.FromDate)
            .NotNull()
            .When(x => x.Cadence == DesktopSalesReviewCadence.Custom)
            .WithMessage("A custom review needs the day it starts.");

        RuleForEach(x => x.Recipients)
            .EmailAddress()
            .WithMessage("'{PropertyValue}' is not an email address.");
    }
}
