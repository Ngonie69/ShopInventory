using FluentValidation;

namespace ShopInventory.Features.Reports.Queries.GetAccountSalesPaymentReport;

public sealed class GetAccountSalesPaymentReportValidator : AbstractValidator<GetAccountSalesPaymentReportQuery>
{
    public GetAccountSalesPaymentReportValidator()
    {
        RuleFor(x => x.Grouping)
            .IsInEnum();

        RuleFor(x => x.AccountCodes)
            .NotEmpty()
            .WithMessage("At least one account code is required.");

        RuleForEach(x => x.AccountCodes)
            .NotEmpty()
            .MaximumLength(50);

        RuleFor(x => x.ToDate)
            .GreaterThanOrEqualTo(x => x.FromDate)
            .When(x => x.FromDate.HasValue && x.ToDate.HasValue)
            .WithMessage("To date must be on or after from date.");
    }
}