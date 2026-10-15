using FluentValidation;
using ShopInventory.Features.DesktopIntegration.Commands.UpdateDesktopSalesReviewSchedule;

namespace ShopInventory.Features.ManagementSales.Commands.UpdateSalesPerformanceEmailSchedule;

public sealed class UpdateSalesPerformanceEmailScheduleValidator : AbstractValidator<UpdateSalesPerformanceEmailScheduleCommand>
{
    public UpdateSalesPerformanceEmailScheduleValidator()
    {
        RuleFor(x => x.CallerUserId).NotEmpty();

        RuleFor(x => x.Recipients)
            .NotNull()
            .Must(list => list.Count <= UpdateDesktopSalesReviewScheduleValidator.MaxRecipients)
            .WithMessage($"At most {UpdateDesktopSalesReviewScheduleValidator.MaxRecipients} people can receive the email.");

        RuleForEach(x => x.Recipients)
            .EmailAddress()
            .WithMessage("'{PropertyValue}' is not an email address.");

        RuleFor(x => x.Recipients)
            .Must(list => list.Any(address => !string.IsNullOrWhiteSpace(address)))
            .When(x => x.DailyEnabled || x.WeeklyEnabled || x.MonthlyEnabled)
            .WithMessage("Name at least one person to send the email to before switching it on.");
    }
}
