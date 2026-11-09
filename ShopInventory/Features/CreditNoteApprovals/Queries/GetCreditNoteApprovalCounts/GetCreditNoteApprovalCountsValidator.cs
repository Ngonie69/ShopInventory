using FluentValidation;

namespace ShopInventory.Features.CreditNoteApprovals.Queries.GetCreditNoteApprovalCounts;

public sealed class GetCreditNoteApprovalCountsValidator : AbstractValidator<GetCreditNoteApprovalCountsQuery>
{
    public GetCreditNoteApprovalCountsValidator()
    {
        RuleFor(query => query.Filter!)
            .SetValidator(new CreditNoteApprovalRequestFilterValidator())
            .When(query => query.Filter is not null);
    }
}
