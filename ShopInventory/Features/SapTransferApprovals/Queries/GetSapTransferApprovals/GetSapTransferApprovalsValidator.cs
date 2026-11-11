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

        // Each code becomes one `eq` term in the filter, so the lists are kept to what a page offers.
        RuleFor(query => query.Filter!.FromWarehouses)
            .Must(BeWarehouseCodes)
            .When(query => query.Filter?.FromWarehouses is not null)
            .WithMessage("Pick at most 20 source warehouses, each a SAP warehouse code.");

        RuleFor(query => query.Filter!.ToWarehouses)
            .Must(BeWarehouseCodes)
            .When(query => query.Filter?.ToWarehouses is not null)
            .WithMessage("Pick at most 20 destination warehouses, each a SAP warehouse code.");

        RuleFor(query => query.Filter!)
            .Must(filter => filter.CreatedFrom is null || filter.CreatedTo is null || filter.CreatedFrom.Value.Date <= filter.CreatedTo.Value.Date)
            .When(query => query.Filter is not null)
            .WithMessage("The created-from day must not be after the created-to day.");

        // An enum from the query string also binds from a bare number, which may name no kind at all.
        RuleFor(query => query.Filter!.Kind)
            .IsInEnum()
            .When(query => query.Filter?.Kind is not null)
            .WithMessage("The kind is transfer or request; leave it out to read both.");

        RuleFor(query => query.Filter!.DraftNum)
            .GreaterThan(0)
            .When(query => query.Filter?.DraftNum is not null)
            .WithMessage("A draft number is a positive whole number.");
    }

    private static bool BeWarehouseCodes(IReadOnlyCollection<string>? codes) =>
        codes is null || (codes.Count <= 20 && codes.All(code => !string.IsNullOrWhiteSpace(code) && code.Trim().Length <= 20));
}
