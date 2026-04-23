using ErrorOr;
using MediatR;
using ShopInventory.Common.Validation;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.DesktopIntegration.Commands.CreateTransfer;

public sealed class CreateTransferHandler(
    ApplicationDbContext context,
    ISAPServiceLayerClient sapClient,
    IStockValidationService stockValidation,
    IOptions<SAPSettings> sapSettings,
    ILogger<CreateTransferHandler> logger
) : IRequestHandler<CreateTransferCommand, ErrorOr<InventoryTransferCreatedResponseDto>>
{
    public async Task<ErrorOr<InventoryTransferCreatedResponseDto>> Handle(
        CreateTransferCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!sapSettings.Value.Enabled)
                return Errors.DesktopIntegration.SapDisabled;

            var request = command.Request;

            logger.LogInformation("Desktop app creating direct transfer: From={From}, To={To}, Lines={Lines}",
                request.FromWarehouse, request.ToWarehouse, request.Lines.Count);

            var sapRequest = new CreateInventoryTransferRequest
            {
                FromWarehouse = request.FromWarehouse,
                ToWarehouse = request.ToWarehouse,
                DocDate = request.DocDate,
                DueDate = request.DueDate,
                Comments = request.Comments,
                Lines = request.Lines.Select(l => new CreateInventoryTransferLineRequest
                {
                    ItemCode = l.ItemCode,
                    Quantity = l.Quantity,
                    UoMCode = l.UoMCode,
                    FromWarehouseCode = l.FromWarehouseCode ?? request.FromWarehouse,
                    ToWarehouseCode = l.WarehouseCode ?? request.ToWarehouse,
                    BatchNumbers = l.BatchNumbers,
                }).ToList()
            };

            var quantityErrors = await UomQuantityValidation.ValidateAndNormalizeLineQuantitiesAsync(
                context,
                sapRequest.Lines,
                line => line.ItemCode,
                line => line.Quantity,
                line => line.UoMCode,
                (line, uomCode) => line.UoMCode = uomCode,
                cancellationToken);

            if (quantityErrors.Count > 0)
                return Errors.DesktopIntegration.ValidationFailed(string.Join("; ", quantityErrors));

            var validationResult = await stockValidation.ValidateInventoryTransferStockAsync(
                sapRequest, cancellationToken);

            if (!validationResult.IsValid)
                return Errors.DesktopIntegration.ValidationFailed(
                    string.Join("; ", validationResult.Errors.Select(e => e.Message)));

            var transfer = await sapClient.CreateInventoryTransferAsync(sapRequest, cancellationToken);

            return new InventoryTransferCreatedResponseDto { Transfer = transfer.ToDto() };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating direct transfer");
            return Errors.DesktopIntegration.TransferFailed(ex.Message);
        }
    }
}
