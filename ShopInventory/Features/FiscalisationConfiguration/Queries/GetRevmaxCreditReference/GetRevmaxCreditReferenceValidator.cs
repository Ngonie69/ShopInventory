using FluentValidation;

namespace ShopInventory.Features.FiscalisationConfiguration.Queries.GetRevmaxCreditReference;

public sealed class GetRevmaxCreditReferenceValidator : AbstractValidator<GetRevmaxCreditReferenceQuery>
{
    public GetRevmaxCreditReferenceValidator()
    {
        RuleFor(x => x.InvoiceDocNum)
            .GreaterThan(0).WithMessage("Invoice number must be a positive SAP DocNum");
    }
}
