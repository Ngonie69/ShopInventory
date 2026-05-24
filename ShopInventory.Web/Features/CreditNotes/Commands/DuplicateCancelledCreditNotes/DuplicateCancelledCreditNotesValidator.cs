using FluentValidation;

namespace ShopInventory.Web.Features.CreditNotes.Commands.DuplicateCancelledCreditNotes;

public sealed class DuplicateCancelledCreditNotesValidator : AbstractValidator<DuplicateCancelledCreditNotesCommand>
{
    public DuplicateCancelledCreditNotesValidator()
    {
        RuleFor(command => command.CreditNoteDocEntries)
            .NotEmpty()
            .WithMessage("Select at least one cancelled credit note to duplicate.")
            .Must(entries => entries.Count <= 100)
            .WithMessage("Duplication is limited to 100 credit notes at a time.");

        RuleForEach(command => command.CreditNoteDocEntries)
            .GreaterThan(0)
            .WithMessage("Credit note document entries must be greater than zero.");
    }
}