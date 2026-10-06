using FluentValidation;

namespace ShopInventory.Features.InventoryTransfers.Commands.EditPendingTransferLines;

public sealed class EditPendingTransferLinesValidator : AbstractValidator<EditPendingTransferLinesCommand>
{
    public EditPendingTransferLinesValidator()
    {
        RuleFor(command => command.Lines)
            .NotEmpty()
            .WithMessage("Name at least one line to change.");

        RuleForEach(command => command.Lines).ChildRules(line =>
        {
            line.RuleFor(item => item.LineNum)
                .GreaterThanOrEqualTo(0)
                .WithMessage("A line number cannot be negative.");
            line.RuleFor(item => item.Quantity)
                .GreaterThanOrEqualTo(0)
                .WithMessage("A quantity cannot be negative; use 0 to take the line out.");
        });

        RuleFor(command => command.Reason)
            .MaximumLength(500)
            .WithMessage("Keep the reason to 500 characters.");
    }
}
