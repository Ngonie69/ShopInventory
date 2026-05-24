using FluentValidation;

namespace ShopInventory.Web.Features.CreditNotes.Commands.DeleteCreditNote;

public sealed class DeleteCreditNoteValidator : AbstractValidator<DeleteCreditNoteCommand>
{
    public DeleteCreditNoteValidator()
    {
        RuleFor(command => command.Id).GreaterThan(0);
    }
}