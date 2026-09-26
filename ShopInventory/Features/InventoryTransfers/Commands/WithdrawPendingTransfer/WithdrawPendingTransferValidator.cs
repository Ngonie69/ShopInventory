using FluentValidation;

namespace ShopInventory.Features.InventoryTransfers.Commands.WithdrawPendingTransfer;

public sealed class WithdrawPendingTransferValidator : AbstractValidator<WithdrawPendingTransferCommand>
{
    public WithdrawPendingTransferValidator()
    {
        RuleFor(command => command.Reason)
            .NotEmpty()
            .WithMessage("Say why the transfer is being withdrawn.")
            .MaximumLength(500)
            .WithMessage("Keep the reason to 500 characters.");
    }
}
