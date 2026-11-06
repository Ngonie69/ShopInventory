using FluentValidation;

namespace ShopInventory.Features.CreditNotes.Queries.DownloadCreditNotePdf;

public sealed class DownloadCreditNotePdfValidator : AbstractValidator<DownloadCreditNotePdfQuery>
{
    public DownloadCreditNotePdfValidator()
    {
        RuleFor(query => query.DocEntry)
            .GreaterThan(0)
            .WithMessage("Name the credit note by its SAP DocEntry.");

        RuleFor(query => query.CardCode)
            .MaximumLength(50);
    }
}
