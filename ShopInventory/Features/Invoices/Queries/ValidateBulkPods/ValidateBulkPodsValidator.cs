using FluentValidation;

namespace ShopInventory.Features.Invoices.Queries.ValidateBulkPods;

public sealed class ValidateBulkPodsValidator : AbstractValidator<ValidateBulkPodsQuery>
{
    public ValidateBulkPodsValidator()
    {
        RuleFor(x => x)
            .Must(x => x.DocNums.Count > 0 || x.SalesOrderDocNums.Count > 0)
            .WithMessage("At least one invoice or sales order number is required.");

        RuleFor(x => x)
            .Must(x => x.DocNums.Count + x.SalesOrderDocNums.Count <= 1500)
            .WithMessage("Bulk POD validation is limited to 1500 lookup numbers at a time.");

        RuleForEach(x => x.DocNums)
            .GreaterThan(0)
            .WithMessage("Invoice numbers must be greater than zero.");

        RuleForEach(x => x.SalesOrderDocNums)
            .GreaterThan(0)
            .WithMessage("Sales order numbers must be greater than zero.");
    }
}