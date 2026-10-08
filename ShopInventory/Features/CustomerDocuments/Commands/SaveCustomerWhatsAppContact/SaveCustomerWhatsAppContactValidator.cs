using FluentValidation;

namespace ShopInventory.Features.CustomerDocuments.Commands.SaveCustomerWhatsAppContact;

/// <summary>
/// Shape only. Whether the number is one, whether the customer exists and whether it is a selling
/// account are the handler's, because they need the configured country code, SAP and the database.
/// </summary>
public sealed class SaveCustomerWhatsAppContactValidator : AbstractValidator<SaveCustomerWhatsAppContactCommand>
{
    public SaveCustomerWhatsAppContactValidator()
    {
        RuleFor(command => command.UserId).NotEmpty();

        RuleFor(command => command.Request.Phone)
            .NotEmpty().WithMessage("Type the customer's WhatsApp number.")
            .MaximumLength(40);

        RuleFor(command => command.Request.CardCode).MaximumLength(50);
        RuleFor(command => command.Request.ContactName).MaximumLength(100);
        RuleFor(command => command.Request.ConsentNote).MaximumLength(500);
        RuleFor(command => command.Request.OwnerName).MaximumLength(200);

        RuleFor(command => command.Request.AlsoApplyToCardCodes)
            .Must(codes => codes is null || codes.Count <= 10)
            .WithMessage("A number can be copied to at most 10 other cards at once.");

        RuleForEach(command => command.Request.AlsoApplyToCardCodes).MaximumLength(50);
    }
}
