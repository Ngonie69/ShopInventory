using FluentValidation;

namespace ShopInventory.Features.FactoryTransfers.Queries.GetFactoryTransferOptions;

public sealed class GetFactoryTransferOptionsValidator : AbstractValidator<GetFactoryTransferOptionsQuery>
{
    public GetFactoryTransferOptionsValidator()
    {
        RuleFor(query => query.UserId).NotEmpty();
    }
}
