using FluentValidation;

namespace ShopInventory.Features.FactoryTransfers.Commands.SendFactoryTransfer;

public sealed class SendFactoryTransferValidator : AbstractValidator<SendFactoryTransferCommand>
{
    public const int MaxLines = 100;

    public SendFactoryTransferValidator()
    {
        RuleFor(command => command.UserId).NotEmpty();
        RuleFor(command => command.ClientRequestId).NotEmpty().MaximumLength(100);
        RuleFor(command => command.SourceWarehouseCode).NotEmpty().MaximumLength(50);
        RuleFor(command => command.DestinationWarehouseCode).MaximumLength(50);
        RuleFor(command => command.Reason)
            .NotEmpty().WithMessage("Say why the stock is being sent.")
            .MaximumLength(500);

        RuleFor(command => command.Lines)
            .NotEmpty().WithMessage("Add at least one product.")
            .Must(lines => lines.Count <= MaxLines).WithMessage($"A transfer can carry at most {MaxLines} products.")
            .Must(lines => lines
                    .Select(line => $"{(line.FromWarehouseCode ?? string.Empty).Trim().ToUpperInvariant()}|{(line.ItemCode ?? string.Empty).Trim().ToUpperInvariant()}")
                    .Distinct()
                    .Count() == lines.Count)
            .WithMessage("Each product may appear only once per warehouse; add the quantities together.");

        RuleFor(command => command.VehiclePlate).MaximumLength(20);
        RuleFor(command => command.DriverName).MaximumLength(100);
        RuleFor(command => command.SourceRequestDocEntry).GreaterThan(0).When(command => command.SourceRequestDocEntry is not null);

        RuleForEach(command => command.Lines).ChildRules(line =>
        {
            line.RuleFor(item => item.ItemCode).NotEmpty().MaximumLength(50);
            line.RuleFor(item => item.ItemDescription).MaximumLength(200);
            line.RuleFor(item => item.UoMCode).MaximumLength(20);
            line.RuleFor(item => item.FromWarehouseCode).MaximumLength(50);
            line.RuleFor(item => item.Quantity)
                .GreaterThan(0).WithMessage("Each quantity must be more than zero.")
                .LessThanOrEqualTo(1_000_000m);
        });
    }
}
