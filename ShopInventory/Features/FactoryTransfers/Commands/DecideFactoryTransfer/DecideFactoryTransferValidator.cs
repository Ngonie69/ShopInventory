using FluentValidation;

namespace ShopInventory.Features.FactoryTransfers.Commands.DecideFactoryTransfer;

public sealed class DecideFactoryTransferValidator : AbstractValidator<DecideFactoryTransferCommand>
{
    public DecideFactoryTransferValidator()
    {
        RuleFor(command => command.TransferId).GreaterThan(0);
        RuleFor(command => command.UserId).NotEmpty();
        RuleFor(command => command.Reason).MaximumLength(500);

        RuleFor(command => command.Decision)
            .Must(decision => string.Equals(decision?.Trim(), DecideFactoryTransferCommand.Accept, StringComparison.OrdinalIgnoreCase)
                || string.Equals(decision?.Trim(), DecideFactoryTransferCommand.Reject, StringComparison.OrdinalIgnoreCase))
            .WithMessage("The decision must be Accept or Reject.");

        When(command => !command.IsAccept, () =>
            RuleFor(command => command.Reason)
                .NotEmpty().WithMessage("Say why the transfer is rejected. Everything goes back to the sender."));

        When(command => command.IsAccept, () =>
        {
            RuleFor(command => command.Lines)
                .NotEmpty().WithMessage("Enter the quantity accepted on each line.")
                .Must(lines => lines.Select(line => line.LineId).Distinct().Count() == lines.Count)
                .WithMessage("Each line may be accepted only once.");

            RuleForEach(command => command.Lines).ChildRules(line =>
            {
                line.RuleFor(item => item.LineId).GreaterThan(0);
                line.RuleFor(item => item.AcceptedQuantity)
                    .GreaterThanOrEqualTo(0).WithMessage("An accepted quantity cannot be negative.");
            });
        });
    }
}
