using FluentValidation;

namespace ShopInventory.Features.FactoryTransfers.Commands.CancelFactoryTransfer;

public sealed class CancelFactoryTransferValidator : AbstractValidator<CancelFactoryTransferCommand>
{
    public CancelFactoryTransferValidator()
    {
        RuleFor(command => command.TransferId).GreaterThan(0);
        RuleFor(command => command.UserId).NotEmpty();
    }
}
