using FluentValidation;

namespace ShopInventory.Features.DesktopIntegration.Commands.UpdateDesktopSalesReviewSchedule;

public sealed class UpdateDesktopSalesReviewScheduleValidator : AbstractValidator<UpdateDesktopSalesReviewScheduleCommand>
{
    /// <summary>More than this is a mailing list, which belongs in the mail server, not here.</summary>
    public const int MaxRecipients = 20;

    public UpdateDesktopSalesReviewScheduleValidator()
    {
        RuleFor(x => x.CallerUserId).NotEmpty();

        RuleFor(x => x.Recipients)
            .NotNull()
            .Must(list => list.Count <= MaxRecipients)
            .WithMessage($"At most {MaxRecipients} people can receive the review.");

        RuleForEach(x => x.Recipients)
            .EmailAddress()
            .WithMessage("'{PropertyValue}' is not an email address.");

        RuleFor(x => x.Recipients)
            .Must(list => list.Any(address => !string.IsNullOrWhiteSpace(address)))
            .When(x => x.WeeklyEnabled || x.MonthlyEnabled)
            .WithMessage("Name at least one person to send the review to before switching it on.");
    }
}
