using FluentValidation;

namespace ShopInventory.Features.FactoryTransfers.Queries.GetFactoryTransfer;

public sealed class GetFactoryTransferValidator : AbstractValidator<GetFactoryTransferQuery>
{
    public GetFactoryTransferValidator()
    {
        RuleFor(query => query.TransferId).GreaterThan(0);
        RuleFor(query => query.UserId).NotEmpty();
    }
}
