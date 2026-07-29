using FluentValidation;

namespace ShopInventory.Web.Features.Revmax.Queries.GetCrossDeviceCreditNoteDraft;

public sealed class GetCrossDeviceCreditNoteDraftValidator : AbstractValidator<GetCrossDeviceCreditNoteDraftQuery>
{
    public GetCrossDeviceCreditNoteDraftValidator()
    {
        RuleFor(x => x.CreditNoteNumber)
            .NotEmpty()
            .MaximumLength(100);

        RuleFor(x => x.OriginalInvoiceNumberOverride)
            .MaximumLength(100)
            .When(x => !string.IsNullOrWhiteSpace(x.OriginalInvoiceNumberOverride));
    }
}