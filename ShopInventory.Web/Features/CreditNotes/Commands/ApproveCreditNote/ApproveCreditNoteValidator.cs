using FluentValidation;

namespace ShopInventory.Web.Features.CreditNotes.Commands.ApproveCreditNote;

public sealed class ApproveCreditNoteValidator : AbstractValidator<ApproveCreditNoteCommand>
{
    public ApproveCreditNoteValidator()
    {
        RuleFor(command => command.Id).GreaterThan(0);
    }
}