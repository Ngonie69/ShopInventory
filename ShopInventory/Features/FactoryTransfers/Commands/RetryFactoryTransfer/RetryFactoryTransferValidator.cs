using FluentValidation;

namespace ShopInventory.Features.FactoryTransfers.Commands.RetryFactoryTransfer;

public sealed class RetryFactoryTransferValidator : AbstractValidator<RetryFactoryTransferCommand>
{
    public RetryFactoryTransferValidator()
    {
        RuleFor(command => command.TransferId).GreaterThan(0);
        RuleFor(command => command.UserId).NotEmpty();
    }
}
