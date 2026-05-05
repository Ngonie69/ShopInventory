using FluentValidation;
using ShopInventory.Features.Notifications;

namespace ShopInventory.Features.Notifications.Commands.CreateNotification;

public sealed class CreateNotificationValidator : AbstractValidator<CreateNotificationCommand>
{
    public CreateNotificationValidator()
    {
        RuleFor(x => x.Request.ActionUrl)
            .Must((command, actionUrl) =>
                !NotificationAudienceRules.CategoryRequiresActionUrl(command.Request.Category) ||
                !string.IsNullOrWhiteSpace(NotificationAudienceRules.NormalizeActionUrl(actionUrl)))
            .WithMessage("ActionUrl is required for module notification categories.");

        RuleFor(x => x.Request.ActionUrl)
            .Must((command, actionUrl) =>
                !NotificationAudienceRules.CategoryRequiresActionUrl(command.Request.Category) ||
                NotificationAudienceRules.GetActionUrlAudienceRoles(actionUrl).Length > 0)
            .When(x => !string.IsNullOrWhiteSpace(x.Request.ActionUrl))
            .WithMessage("ActionUrl must point to a supported staff module route.");
    }
}