using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.DesktopIntegration.Commands.CreateTransferRequest;

public sealed class CreateTransferRequestHandler(
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
                    FromWarehouseCode = l.FromWarehouseCode ?? request.FromWarehouse,
                    ToWarehouseCode = l.ToWarehouseCode ?? request.ToWarehouse
                }).ToList()
            };

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
