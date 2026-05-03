using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.ExceptionCenter.Commands.RetryExceptionCenterItem;

public sealed class RetryExceptionCenterItemHandler(
    ApplicationDbContext context,
    IInvoiceQueueService invoiceQueueService,
    IInventoryTransferQueueService inventoryTransferQueueService,
    ILogger<RetryExceptionCenterItemHandler> logger
) : IRequestHandler<RetryExceptionCenterItemCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        RetryExceptionCenterItemCommand command,
        CancellationToken cancellationToken)
    {
        var source = command.Source.Trim().ToLowerInvariant();

        return source switch
        {
            "invoice-queue" => await RetryInvoiceAsync(command.ItemId, cancellationToken),
            "inventory-transfer-queue" => await RetryTransferAsync(command.ItemId, cancellationToken),
            "mobile-order-post-processing" => await RetryMobileAsync(command.ItemId, cancellationToken),
            "payment-callback" => Errors.ExceptionCenter.RetryNotSupported(command.Source),
            "payment-callback-rejection" => Errors.ExceptionCenter.RetryNotSupported(command.Source),
            "credit-note-fiscalization" => Errors.ExceptionCenter.RetryNotSupported(command.Source),
            _ => Errors.ExceptionCenter.ItemNotFound(command.Source, command.ItemId)
        };

        async Task<ErrorOr<Success>> RetryInvoiceAsync(int itemId, CancellationToken token)
        {
            var externalReference = await context.InvoiceQueue
                .AsNoTracking()
                .Where(q => q.Id == itemId)
                .Select(q => q.ExternalReference)
                .FirstOrDefaultAsync(token);

            if (string.IsNullOrWhiteSpace(externalReference))
            {
                return Errors.ExceptionCenter.ItemNotFound(command.Source, itemId);
            }

            var success = await invoiceQueueService.RetryInvoiceAsync(externalReference, token);
            if (!success)
            {
                return Errors.ExceptionCenter.ItemNotFound(command.Source, itemId);
            }

            logger.LogInformation("Exception center retried invoice queue item {ItemId} ({ExternalReference})", itemId, externalReference);
            return Result.Success;
        }

        async Task<ErrorOr<Success>> RetryTransferAsync(int itemId, CancellationToken token)
        {
            var externalReference = await context.InventoryTransferQueue
                .AsNoTracking()
                .Where(q => q.Id == itemId)
                .Select(q => q.ExternalReference)
                .FirstOrDefaultAsync(token);

            if (string.IsNullOrWhiteSpace(externalReference))
            {
                return Errors.ExceptionCenter.ItemNotFound(command.Source, itemId);
            }

            var success = await inventoryTransferQueueService.RetryTransferAsync(externalReference, token);
            if (!success)
            {
                return Errors.ExceptionCenter.ItemNotFound(command.Source, itemId);
            }

            logger.LogInformation("Exception center retried transfer queue item {ItemId} ({ExternalReference})", itemId, externalReference);
            return Result.Success;
        }

        async Task<ErrorOr<Success>> RetryMobileAsync(int itemId, CancellationToken token)
        {
            var entry = await context.MobileOrderPostProcessingQueue
                .FirstOrDefaultAsync(q => q.Id == itemId, token);

            if (entry == null)
            {
                return Errors.ExceptionCenter.ItemNotFound(command.Source, itemId);
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

            logger.LogInformation("Exception center retried mobile post-processing item {ItemId}", itemId);
            return Result.Success;
        }
    }
}