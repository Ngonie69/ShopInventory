using FluentValidation;
using ShopInventory.Common.Crates;

namespace ShopInventory.Features.Crates.Queries.ValidateBulkCratePods;

public sealed class ValidateBulkCratePodsValidator : AbstractValidator<ValidateBulkCratePodsQuery>
{
    public ValidateBulkCratePodsValidator()
    {
        RuleFor(x => x.InvoiceDocNums)
            .NotEmpty()
            .WithMessage("Provide at least one invoice number to validate.")
            .Must(docNums => docNums.Count <= 1500)
            .WithMessage("Bulk crate POD validation is limited to 1500 invoice numbers at a time.");

        RuleForEach(x => x.InvoiceDocNums)
            .GreaterThan(0)
            .WithMessage("Invoice numbers must be positive integers.");

        RuleFor(x => x.SubmissionRole)
            .Must(role =>
                string.IsNullOrWhiteSpace(role) ||
                string.Equals(role, CrateTrackingConstants.SubmissionRoleDriver, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(role, CrateTrackingConstants.SubmissionRoleMerchandiser, StringComparison.OrdinalIgnoreCase))
            .WithMessage("Submission role must be Driver or Merchandiser.");
    }
}