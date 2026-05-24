using FluentValidation;

namespace ShopInventory.Web.Features.Revmax.Commands.FiscalizeCrossDeviceCreditNote;

public sealed class FiscalizeCrossDeviceCreditNoteValidator : AbstractValidator<FiscalizeCrossDeviceCreditNoteCommand>
{
    public FiscalizeCrossDeviceCreditNoteValidator()
    {
        RuleFor(x => x.CreditNoteNumber)
            .NotEmpty()
            .MaximumLength(100);

        RuleFor(x => x.OriginalInvoiceNumber)
            .NotEmpty()
            .MaximumLength(100);

        RuleFor(x => x.InvoiceComment)
            .NotEmpty()
            .WithMessage("Reason / comment is required for a credit note fiscalization.")
            .MaximumLength(500);

        RuleFor(x => x.RefDeviceId)
            .GreaterThan(0)
            .When(x => x.RefDeviceId.HasValue);

        RuleFor(x => x.RefReceiptGlobalNo)
            .GreaterThan(0)
            .When(x => x.RefReceiptGlobalNo.HasValue);

        RuleFor(x => x.RefFiscalDayNo)
            .GreaterThan(0)
            .When(x => x.RefFiscalDayNo.HasValue);
    }
}