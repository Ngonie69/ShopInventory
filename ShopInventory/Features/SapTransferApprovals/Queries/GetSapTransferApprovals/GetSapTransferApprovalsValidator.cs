using FluentValidation;

namespace ShopInventory.Features.SapTransferApprovals.Queries.GetSapTransferApprovals;

public sealed class GetSapTransferApprovalsValidator : AbstractValidator<GetSapTransferApprovalsQuery>
{
    public GetSapTransferApprovalsValidator()
    {
        RuleFor(query => query.Page).GreaterThanOrEqualTo(1);
        RuleFor(query => query.PageSize).InclusiveBetween(1, 100);

        // The cursor is a SAP draft DocEntry, and it goes into the OData filter as a literal.
        RuleFor(query => query.BeforeDocEntry)
            .GreaterThan(0)
            .When(query => query.BeforeDocEntry is not null)
            .WithMessage("The paging cursor must be a SAP draft DocEntry.");

        // SAP warehouse codes are eight characters; the value is escaped into the filter either way.
        RuleFor(query => query.Warehouse)
            .MaximumLength(20)
            .When(query => !string.IsNullOrWhiteSpace(query.Warehouse));
    }
}
