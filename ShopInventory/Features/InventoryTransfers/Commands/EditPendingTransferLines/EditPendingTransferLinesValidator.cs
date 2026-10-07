using FluentValidation;

namespace ShopInventory.Features.InventoryTransfers.Commands.EditPendingTransferLines;

public sealed class EditPendingTransferLinesValidator : AbstractValidator<EditPendingTransferLinesCommand>
{
    public EditPendingTransferLinesValidator()
    {
        RuleFor(command => command)
            .Must(command => command.Lines is { Count: > 0 } || command.AddedLines is { Count: > 0 })
            .WithName("Lines")
            .WithMessage("Name at least one line to change or one item to add.");

        RuleForEach(command => command.Lines).ChildRules(line =>
        {
            line.RuleFor(item => item.LineNum)
                .GreaterThanOrEqualTo(0)
                .WithMessage("A line number cannot be negative.");
            line.RuleFor(item => item.Quantity)
                .GreaterThanOrEqualTo(0)
                .WithMessage("A quantity cannot be negative; use 0 to take the line out.");
        });

        RuleForEach(command => command.AddedLines).ChildRules(line =>
        {
            line.RuleFor(item => item.ItemCode)
                .NotEmpty()
                .WithMessage("An added line needs an item code.");
            line.RuleFor(item => item.Quantity)
                .GreaterThan(0)
                .WithMessage("An added line needs a quantity above zero.");
        });

        RuleFor(command => command.Reason)
            .MaximumLength(500)
            .WithMessage("Keep the reason to 500 characters.");
    }
}
