using FluentValidation;

namespace ShopInventory.Features.CustomerDocuments.Commands.RequestInvoiceWhatsApp;

public sealed class RequestInvoiceWhatsAppValidator : AbstractValidator<RequestInvoiceWhatsAppCommand>
{
    public RequestInvoiceWhatsAppValidator()
    {
        RuleFor(command => command.UserId).NotEmpty();
        RuleFor(command => command.DocEntry).GreaterThan(0);

        RuleFor(command => command.Request.ContactIds)
            .Must(ids => ids is null || ids.Count <= 5)
            .WithMessage("Send to at most 5 saved numbers at once.");

        RuleFor(command => command.Request.OneOffPhone).MaximumLength(40);
        RuleFor(command => command.Request.OneOffName).MaximumLength(100);
    }
}
