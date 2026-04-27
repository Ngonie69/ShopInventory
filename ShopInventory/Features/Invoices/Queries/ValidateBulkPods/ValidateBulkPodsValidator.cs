using FluentValidation;

namespace ShopInventory.Features.Invoices.Queries.ValidateBulkPods;

public sealed class ValidateBulkPodsValidator : AbstractValidator<ValidateBulkPodsQuery>
{
    public ValidateBulkPodsValidator()
    {
        RuleFor(x => x.DocNums)
            .NotEmpty()
            .WithMessage("At least one invoice number is required.")
            .Must(docNums => docNums.Count <= 1500)
            .WithMessage("Bulk POD validation is limited to 1500 invoice numbers at a time.");

        RuleForEach(x => x.DocNums)
            .GreaterThan(0)
            .WithMessage("Invoice numbers must be greater than zero.");
    }
}