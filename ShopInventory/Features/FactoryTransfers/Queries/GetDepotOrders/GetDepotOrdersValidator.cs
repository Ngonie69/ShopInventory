using FluentValidation;

namespace ShopInventory.Features.FactoryTransfers.Queries.GetDepotOrders;

public sealed class GetDepotOrdersValidator : AbstractValidator<GetDepotOrdersQuery>
{
    public GetDepotOrdersValidator()
    {
        RuleFor(query => query.UserId).NotEmpty();
        RuleFor(query => query.DepotCode).NotEmpty().MaximumLength(50);
    }
}
