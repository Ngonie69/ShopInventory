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
using Microsoft.EntityFrameworkCore;
using ShopInventory.Models;

namespace ShopInventory.Features.DesktopIntegration.Commands.CreateTransferRequest;

public sealed class CreateTransferRequestHandler(
    ApplicationDbContext context,
    ISAPServiceLayerClient sapClient,
    IAuditService auditService,
    IInventoryTransferApprovalService approvalService,
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
            User? requestingUser = null;
            if (Guid.TryParse(command.CreatedBy, out var requestingUserId))
            {
                requestingUser = await context.Users
                    .AsNoTracking()
                    .FirstOrDefaultAsync(user => user.Id == requestingUserId && user.IsActive, cancellationToken);
            }

            logger.LogInformation("Desktop app creating transfer request: From={From}, To={To}, CreatedBy={CreatedBy}",
                request.FromWarehouse, request.ToWarehouse, command.CreatedBy);

            var sapRequest = new CreateTransferRequestDto
            {
                FromWarehouse = request.FromWarehouse,
                ToWarehouse = request.ToWarehouse,
                DocDate = request.DocDate,
                DueDate = request.DueDate,
                Comments = request.Comments,
                RequesterEmail = requestingUser?.Email ?? request.RequesterEmail,
                RequesterName = requestingUser is null
                    ? request.RequesterName ?? command.CreatedBy
                    : string.Join(' ', new[] { requestingUser.FirstName, requestingUser.LastName }
                        .Where(value => !string.IsNullOrWhiteSpace(value))),
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

            if (string.IsNullOrWhiteSpace(sapRequest.RequesterName) && requestingUser is not null)
                sapRequest.RequesterName = requestingUser.Username;

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
            await approvalService.EnsureRequestAsync(transferRequest, requestingUser?.Id, cancellationToken);

            try
            {
                await auditService.LogAsync(
                    AuditActions.CreateTransferRequest,
                    "TransferRequest",
                    transferRequest.DocEntry.ToString(),
                    $"Transfer request #{transferRequest.DocNum} from {request.FromWarehouse} to {request.ToWarehouse}",
                    true);
            }
            catch
            {
            }

            return transferRequest.ToDto();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating transfer request");
            return Errors.DesktopIntegration.TransferRequestFailed(ex.Message);
        }
    }
}
