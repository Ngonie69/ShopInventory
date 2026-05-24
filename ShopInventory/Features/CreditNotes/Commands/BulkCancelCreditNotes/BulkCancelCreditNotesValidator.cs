using FluentValidation;

namespace ShopInventory.Features.CreditNotes.Commands.BulkCancelCreditNotes;

public sealed class BulkCancelCreditNotesValidator : AbstractValidator<BulkCancelCreditNotesCommand>
{
    public BulkCancelCreditNotesValidator()
    {
        RuleFor(command => command.Request.CreditNoteDocEntries)
            .NotEmpty()
            .WithMessage("Select at least one credit note to cancel.")
            .Must(entries => entries.Count <= 100)
            .WithMessage("Bulk cancellation is limited to 100 credit notes at a time.");

        RuleForEach(command => command.Request.CreditNoteDocEntries)
            .GreaterThan(0)
            .WithMessage("Credit note document entries must be greater than zero.");
    }
}