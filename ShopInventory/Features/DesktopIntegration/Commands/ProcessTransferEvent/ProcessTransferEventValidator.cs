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
        // Negative is an SAP cancellation, the original movement reversed — see TransferCancellation.
        RuleFor(x => x.Quantity).NotEqual(0);
    }
}
