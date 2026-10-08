using FluentValidation;

namespace ShopInventory.Features.CustomerDocuments.Commands.UpdateCustomerDocumentDeliverySettings;

public sealed class UpdateCustomerDocumentDeliverySettingsValidator : AbstractValidator<UpdateCustomerDocumentDeliverySettingsCommand>
{
    public UpdateCustomerDocumentDeliverySettingsValidator()
    {
        RuleFor(command => command.UserId).NotEmpty();
        RuleFor(command => command.Request.WhatsAppSessionId).MaximumLength(64);
        RuleFor(command => command.Request.MaxAutoPerDay)
            .InclusiveBetween(0, 1000)
            .WithMessage("The automatic cap must be between 0 and 1,000 a day.");
    }
}
