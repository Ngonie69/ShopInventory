using FluentValidation;

namespace ShopInventory.Features.CreditNoteApprovals.Queries.GetCreditNoteApprovals;

public sealed class GetCreditNoteApprovalsValidator : AbstractValidator<GetCreditNoteApprovalsQuery>
{
    public GetCreditNoteApprovalsValidator()
    {
        RuleFor(query => query.Status)
            .Must(CreditNoteApprovalStatusFilters.IsKnown)
            .WithMessage($"Status must be one of: {string.Join(", ", CreditNoteApprovalStatusFilters.Known)}.");

        RuleFor(query => query.Page).GreaterThanOrEqualTo(1);
        RuleFor(query => query.PageSize).InclusiveBetween(1, 100);
    }
}
