using FluentValidation;

namespace ShopInventory.Features.CustomerDocuments.Commands.UpdateCustomerWhatsAppContact;

public sealed class UpdateCustomerWhatsAppContactValidator : AbstractValidator<UpdateCustomerWhatsAppContactCommand>
{
    public UpdateCustomerWhatsAppContactValidator()
    {
        RuleFor(command => command.UserId).NotEmpty();
        RuleFor(command => command.ContactId).GreaterThan(0);
        RuleFor(command => command.Request.ContactName).MaximumLength(100);
    }
}
