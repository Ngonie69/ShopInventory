using FluentValidation;

namespace ShopInventory.Features.FactoryTransfers.Commands.RecallFactoryTransfer;

public sealed class RecallFactoryTransferValidator : AbstractValidator<RecallFactoryTransferCommand>
{
    public RecallFactoryTransferValidator()
    {
        RuleFor(command => command.TransferId).GreaterThan(0);
        RuleFor(command => command.UserId).NotEmpty();
        RuleFor(command => command.Reason)
            .NotEmpty().WithMessage("Say why the transfer is being recalled.")
            .MaximumLength(500);
    }
}
