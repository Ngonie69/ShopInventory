using FluentValidation;

namespace ShopInventory.Web.Features.Batches.Queries.GetBatchStatusHistory;

public sealed class GetBatchStatusHistoryValidator : AbstractValidator<GetBatchStatusHistoryQuery>
{
    public GetBatchStatusHistoryValidator()
    {
        RuleFor(x => x.Page)
            .GreaterThan(0)
            .WithMessage("Page must be greater than zero.");

        RuleFor(x => x.PageSize)
            .InclusiveBetween(1, 100)
            .WithMessage("Page size must be between 1 and 100.");
    }
}