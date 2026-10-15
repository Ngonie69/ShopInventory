using FluentValidation;
using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesReview;
using ShopInventory.Features.ManagementSales.Queries.GetSalesPerformanceEmailSchedule;

namespace ShopInventory.Features.ManagementSales.Commands.SendSalesPerformanceEmail;

public sealed class SendSalesPerformanceEmailValidator : AbstractValidator<SendSalesPerformanceEmailCommand>
{
    public SendSalesPerformanceEmailValidator()
    {
        RuleFor(x => x.Cadence)
            .Must(SalesPerformanceEmailCadence.IsKnown)
            .WithMessage("The period must be daily, weekly, monthly or custom.");

        RuleFor(x => x.CallerUserId)
            .NotEmpty()
            .When(x => !x.Scheduled)
            .WithMessage("An email sent by hand is read as the person sending it.");

        RuleFor(x => x.FromDate)
            .NotNull()
            .When(x => x.Cadence == SalesPerformanceEmailCadence.Custom)
            .WithMessage("A custom period needs the day it starts.");

        RuleFor(x => x.ToDate)
            .Must((command, to) => to!.Value.Date >= command.FromDate!.Value.Date
                                   && (to.Value.Date - command.FromDate.Value.Date).TotalDays < GetDesktopSalesReviewValidator.MaxDays)
            .When(x => x.Cadence == SalesPerformanceEmailCadence.Custom && x.FromDate.HasValue && x.ToDate.HasValue)
            .WithMessage($"A custom period must end on or after its first day, and cover fewer than {GetDesktopSalesReviewValidator.MaxDays} days.");

        RuleForEach(x => x.Recipients)
            .EmailAddress()
            .WithMessage("'{PropertyValue}' is not an email address.");
    }
}
