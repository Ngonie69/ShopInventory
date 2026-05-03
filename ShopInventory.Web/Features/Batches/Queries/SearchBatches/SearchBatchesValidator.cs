using FluentValidation;

namespace ShopInventory.Web.Features.Batches.Queries.SearchBatches;

public sealed class SearchBatchesValidator : AbstractValidator<SearchBatchesQuery>
{
    public SearchBatchesValidator()
    {
        RuleFor(x => x.SearchTerm)
            .NotEmpty().WithMessage("Search term is required")
            .MinimumLength(2).WithMessage("Search term must be at least 2 characters long");
    }
}