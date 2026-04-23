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

namespace ShopInventory.Features.DesktopIntegration.Commands.CreateTransferRequest;

public sealed class CreateTransferRequestHandler(
    ApplicationDbContext context,
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> sapSettings,
    ILogger<CreateTransferRequestHandler> logger
) : IRequestHandler<CreateTransferRequestCommand, ErrorOr<InventoryTransferRequestDto>>
{
    public async Task<ErrorOr<InventoryTransferRequestDto>> Handle(
        CreateTransferRequestCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!sapSettings.Value.Enabled)
                return Errors.DesktopIntegration.SapDisabled;

            var request = command.Request;

            logger.LogInformation("Desktop app creating transfer request: From={From}, To={To}, CreatedBy={CreatedBy}",
                request.FromWarehouse, request.ToWarehouse, command.CreatedBy);

            var sapRequest = new CreateTransferRequestDto
            {
                FromWarehouse = request.FromWarehouse,
                ToWarehouse = request.ToWarehouse,
                DocDate = request.DocDate,
                DueDate = request.DueDate,
                Comments = request.Comments,
                RequesterEmail = request.RequesterEmail,
                RequesterName = request.RequesterName ?? command.CreatedBy,
                RequesterBranch = request.RequesterBranch,
                RequesterDepartment = request.RequesterDepartment,
                Lines = request.Lines.Select(l => new CreateTransferRequestLineDto
                {
                    ItemCode = l.ItemCode,
                    Quantity = l.Quantity,
                    UoMCode = l.UoMCode,
                    FromWarehouseCode = l.FromWarehouseCode ?? request.FromWarehouse,
                    ToWarehouseCode = l.ToWarehouseCode ?? request.ToWarehouse
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

            var transferRequest = await sapClient.CreateInventoryTransferRequestAsync(sapRequest, cancellationToken);

            return transferRequest.ToDto();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating transfer request");
            return Errors.DesktopIntegration.TransferRequestFailed(ex.Message);
        }
    }
}
