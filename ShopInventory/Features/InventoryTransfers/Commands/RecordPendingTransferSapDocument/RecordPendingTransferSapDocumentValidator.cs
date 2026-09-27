using FluentValidation;

namespace ShopInventory.Features.InventoryTransfers.Commands.RecordPendingTransferSapDocument;

public sealed class RecordPendingTransferSapDocumentValidator : AbstractValidator<RecordPendingTransferSapDocumentCommand>
{
    public RecordPendingTransferSapDocumentValidator()
    {
        RuleFor(command => command.SapDocNum)
            .GreaterThan(0)
            .WithMessage("Enter the transfer number SAP shows for it.");
    }
}
