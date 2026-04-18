using ErrorOr;
using FluentValidation;

namespace ShopInventory.Features.DesktopIntegration.Commands.ProcessTransferEvent;

public sealed class ProcessTransferEventValidator : AbstractValidator<ProcessTransferEventCommand>
{
    public ProcessTransferEventValidator()
    {
        RuleFor(x => x.ItemCode).NotEmpty();
        RuleFor(x => x.SourceWarehouse).NotEmpty();
        RuleFor(x => x.DestinationWarehouse).NotEmpty();
        RuleFor(x => x.Quantity).GreaterThan(0);
    }
}
