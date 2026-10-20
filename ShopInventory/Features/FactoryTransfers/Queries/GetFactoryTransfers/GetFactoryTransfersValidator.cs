using FluentValidation;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.FactoryTransfers.Queries.GetFactoryTransfers;

public sealed class GetFactoryTransfersValidator : AbstractValidator<GetFactoryTransfersQuery>
{
    public GetFactoryTransfersValidator()
    {
        RuleFor(query => query.UserId).NotEmpty();
        RuleFor(query => query.Page).GreaterThanOrEqualTo(1);
        RuleFor(query => query.PageSize).InclusiveBetween(1, 100);
        RuleFor(query => query.Search).MaximumLength(100);
        RuleFor(query => query.View)
            .Must(view => string.IsNullOrWhiteSpace(view)
                || view.Trim().ToLowerInvariant() is GetFactoryTransfersQuery.Awaiting or GetFactoryTransfersQuery.Open or GetFactoryTransfersQuery.All
                || FactoryTransferStatuses.All.Contains(view.Trim(), StringComparer.OrdinalIgnoreCase))
            .WithMessage("View must be awaiting, open, all, or a status.");
    }
}
