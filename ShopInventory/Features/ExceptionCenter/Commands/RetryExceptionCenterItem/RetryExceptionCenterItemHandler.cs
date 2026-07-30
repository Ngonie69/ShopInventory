using ErrorOr;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Security;
using ShopInventory.Data;
using ShopInventory.Features.InventoryTransfers.Commands.RetryPendingRequestEditApply;
using ShopInventory.Features.InventoryTransfers.Commands.RetryPendingTransferPost;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.ExceptionCenter.Commands.RetryExceptionCenterItem;

public sealed class RetryExceptionCenterItemHandler(
    ApplicationDbContext context,
    IInvoiceQueueService invoiceQueueService,
    IInventoryTransferQueueService inventoryTransferQueueService,
    IMediator mediator,
    IHttpContextAccessor httpContextAccessor,
    ILogger<RetryExceptionCenterItemHandler> logger
) : IRequestHandler<RetryExceptionCenterItemCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        RetryExceptionCenterItemCommand command,
        CancellationToken cancellationToken)
    {
        var source = ExceptionCenterSources.Normalize(command.Source);

        if (!ExceptionCenterSources.IsKnown(source))
        {
            return Errors.ExceptionCenter.ItemNotFound(command.Source, command.ItemKey);
        }

        if (!ExceptionCenterSources.SupportsRetry(source))
        {
            return Errors.ExceptionCenter.RetryNotSupported(command.Source);
        }

        if (ExceptionCenterSources.IsGuidKeyed(source))
        {
            if (!Guid.TryParse(command.ItemKey, out var pendingId))
            {
                return Errors.ExceptionCenter.ItemNotFound(command.Source, command.ItemKey);
            }

            return source switch
            {
                ExceptionCenterSources.PendingInventoryTransferPost => await RetryPendingTransferAsync(pendingId, cancellationToken),
                ExceptionCenterSources.PendingTransferRequestEditApply => await RetryPendingEditAsync(pendingId, cancellationToken),
                _ => Errors.ExceptionCenter.ItemNotFound(command.Source, command.ItemKey)
            };
        }

        if (!int.TryParse(command.ItemKey, out var itemId))
        {
            return Errors.ExceptionCenter.ItemNotFound(command.Source, command.ItemKey);
        }

        return source switch
        {
            ExceptionCenterSources.InvoiceQueue => await RetryInvoiceAsync(itemId, cancellationToken),
            ExceptionCenterSources.InventoryTransferQueue => await RetryTransferAsync(itemId, cancellationToken),
            ExceptionCenterSources.MobileOrderPostProcessing => await RetryMobileAsync(itemId, cancellationToken),
            ExceptionCenterSources.IncomingPaymentQueue => await RetryIncomingPaymentAsync(itemId, cancellationToken),
            _ => Errors.ExceptionCenter.RetryNotSupported(command.Source)
        };

        async Task<ErrorOr<Success>> RetryInvoiceAsync(int id, CancellationToken token)
        {
            var externalReference = await context.InvoiceQueue
                .AsNoTracking()
                .Where(q => q.Id == id)
                .Select(q => q.ExternalReference)
                .FirstOrDefaultAsync(token);

            if (string.IsNullOrWhiteSpace(externalReference))
            {
                return Errors.ExceptionCenter.ItemNotFound(command.Source, command.ItemKey);
            }

            var success = await invoiceQueueService.RetryInvoiceAsync(externalReference, token);
            if (!success)
            {
                return Errors.ExceptionCenter.ItemNotFound(command.Source, command.ItemKey);
            }

            logger.LogInformation("Exception center retried invoice queue item {ItemId} ({ExternalReference})", id, externalReference);
            return Result.Success;
        }

        async Task<ErrorOr<Success>> RetryTransferAsync(int id, CancellationToken token)
        {
            var externalReference = await context.InventoryTransferQueue
                .AsNoTracking()
                .Where(q => q.Id == id)
                .Select(q => q.ExternalReference)
                .FirstOrDefaultAsync(token);

            if (string.IsNullOrWhiteSpace(externalReference))
            {
                return Errors.ExceptionCenter.ItemNotFound(command.Source, command.ItemKey);
            }

            var success = await inventoryTransferQueueService.RetryTransferAsync(externalReference, token);
            if (!success)
            {
                return Errors.ExceptionCenter.ItemNotFound(command.Source, command.ItemKey);
            }

            logger.LogInformation("Exception center retried transfer queue item {ItemId} ({ExternalReference})", id, externalReference);
            return Result.Success;
        }

        async Task<ErrorOr<Success>> RetryMobileAsync(int id, CancellationToken token)
        {
            var entry = await context.MobileOrderPostProcessingQueue
                .AsTracking()
                .FirstOrDefaultAsync(q => q.Id == id, token);

            if (entry == null)
            {
                return Errors.ExceptionCenter.ItemNotFound(command.Source, command.ItemKey);
            }

            if (entry.Status is not MobileOrderPostProcessingQueueStatus.Failed and not MobileOrderPostProcessingQueueStatus.RequiresReview)
            {
                return Errors.ExceptionCenter.RetryNotSupported(command.Source);
            }

            entry.Status = MobileOrderPostProcessingQueueStatus.Pending;
            entry.RetryCount = 0;
            entry.LastError = null;
            entry.NextRetryAt = null;
            entry.ProcessingStartedAt = null;
            entry.ProcessedAt = null;

            await context.SaveChangesAsync(token);

            logger.LogInformation("Exception center retried mobile post-processing item {ItemId}", id);
            return Result.Success;
        }

        // Both approval-gated sources push at SAP under the caller's own identity, so the retry
        // goes through the owning feature's command and inherits its guards — SAP availability,
        // the status check, and the warehouse scope the caller is allowed to act on.
        async Task<ErrorOr<Success>> RetryPendingTransferAsync(Guid id, CancellationToken token)
        {
            var userId = ResolveCurrentUserId();
            if (userId is null)
            {
                return Errors.ExceptionCenter.UpdateFailed("Retry", "Could not resolve the current user for the retry.");
            }

            var result = await mediator.Send(new RetryPendingTransferPostCommand(id, userId.Value), token);
            if (result.IsError)
            {
                return result.Errors;
            }

            logger.LogInformation("Exception center reposted held inventory transfer {PendingTransferId}", id);
            return Result.Success;
        }

        async Task<ErrorOr<Success>> RetryPendingEditAsync(Guid id, CancellationToken token)
        {
            var userId = ResolveCurrentUserId();
            if (userId is null)
            {
                return Errors.ExceptionCenter.UpdateFailed("Retry", "Could not resolve the current user for the retry.");
            }

            var result = await mediator.Send(new RetryPendingRequestEditApplyCommand(id, userId.Value), token);
            if (result.IsError)
            {
                return result.Errors;
            }

            logger.LogInformation("Exception center reapplied held transfer request change {PendingEditId}", id);
            return Result.Success;
        }

        // The incoming payment worker picks up anything Pending, so resetting the entry
        // is enough to requeue it — the same shape as the mobile queue above.
        async Task<ErrorOr<Success>> RetryIncomingPaymentAsync(int id, CancellationToken token)
        {
            var entry = await context.IncomingPaymentQueue
                .AsTracking()
                .FirstOrDefaultAsync(q => q.Id == id, token);

            if (entry == null)
            {
                return Errors.ExceptionCenter.ItemNotFound(command.Source, command.ItemKey);
            }

            if (entry.Status is not IncomingPaymentQueueStatus.Failed and not IncomingPaymentQueueStatus.RequiresReview)
            {
                return Errors.ExceptionCenter.RetryNotSupported(command.Source);
            }

            entry.Status = IncomingPaymentQueueStatus.Pending;
            entry.RetryCount = 0;
            entry.LastError = null;
            entry.NextRetryAt = null;
            entry.ProcessingStartedAt = null;
            entry.ProcessedAt = null;

            await context.SaveChangesAsync(token);

            logger.LogInformation("Exception center retried incoming payment queue item {ItemId} ({ExternalReference})", id, entry.ExternalReference);
            return Result.Success;
        }
    }

    private Guid? ResolveCurrentUserId()
        => UserClaimReader.GetUserId(httpContextAccessor.HttpContext?.User);
}
