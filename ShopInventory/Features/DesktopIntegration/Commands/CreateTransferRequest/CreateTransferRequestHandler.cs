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
        // Set once the post is committed, and read by the cancellation filters below. Past that
        // point the request token no longer governs, so an OperationCanceledException can only be
        // the SAP client's own timeout — a different outcome, needing a different message.
        var postCommitted = false;

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

            // The last safe abort. Everything above is preparation and may be cancelled freely;
            // everything below is a durable obligation and runs on CancellationToken.None.
            //
            // The approval row is what makes the document exist for the app: the Service Layer
            // bypasses B1's own approval procedures, so the local engine is the only control over a
            // transfer request. Creating it in SAP on the request token and then recording it on
            // the same token put a disconnect between those two lines — and a disconnect there left
            // SAP holding a transfer request the approval engine had never heard of, with nothing
            // to find it again: EnsureRequestAsync is idempotent but only the create, edit and
            // convert paths ever call it, and there is no reconciliation job for approvals.
            cancellationToken.ThrowIfCancellationRequested();
            postCommitted = true;

            var transferRequest = await sapClient.CreateInventoryTransferRequestAsync(sapRequest, CancellationToken.None);
            await approvalService.EnsureRequestAsync(transferRequest, requestingUser?.Id, CancellationToken.None);

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
        catch (OperationCanceledException) when (!postCommitted && cancellationToken.IsCancellationRequested)
        {
            // Cancelled while still preparing. Nothing reached SAP, so there is nothing to recover.
            return Errors.DesktopIntegration.TransferRequestFailed("Request was canceled by the client");
        }
        catch (OperationCanceledException ex) when (!postCommitted)
        {
            logger.LogError(ex, "Timeout or connection abort while preparing the transfer request");
            return Errors.DesktopIntegration.TransferRequestFailed(
                "Connection to SAP Service Layer timed out or was aborted.");
        }
        catch (OperationCanceledException ex)
        {
            // Past the commit point nothing can cancel this but the SAP client's own timeout, and
            // the document may well have been created before the reply was lost. Send the caller to
            // check SAP rather than offering a retry that would post it twice.
            logger.LogError(
                ex,
                "Transfer request post to SAP was aborted after it began; the document may exist in SAP");
            return Errors.DesktopIntegration.TransferRequestPostUncertain;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating transfer request");
            return Errors.DesktopIntegration.TransferRequestFailed(ex.Message);
        }
    }
}
