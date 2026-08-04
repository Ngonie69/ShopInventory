using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Features.Notifications;
using ShopInventory.Services;

namespace ShopInventory.Features.Batches.Commands.UpdateBatchStatus;

public sealed class UpdateBatchStatusHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    INotificationService notificationService,
    ILogger<UpdateBatchStatusHandler> logger
) : IRequestHandler<UpdateBatchStatusCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        UpdateBatchStatusCommand command,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
        {
            return Errors.Batch.SapDisabled;
        }

        try
        {
            await sapClient.UpdateBatchStatusAsync(command.BatchEntryId, command.Status, cancellationToken);

            logger.LogInformation(
                "Updated batch {BatchEntryId} to status {Status}",
                command.BatchEntryId,
                command.Status);

            // The Lab audience had a category, an audience rule and a route reserved for it and no
            // producer, so it received no notification of any kind. This is that producer.
            try
            {
                await notificationService.CreateNotificationAsync(
                    ModuleNotificationFactory.CreateBroadcastNotification(
                        $"Batch Status Updated: {command.BatchEntryId}",
                        $"Batch {command.BatchEntryId} was set to {command.Status}.",
                        "Info",
                        "BatchStatus",
                        "Batch",
                        command.BatchEntryId.ToString(),
                        "/lab/batch-status",
                        new Dictionary<string, string>
                        {
                            ["batchEntryId"] = command.BatchEntryId.ToString(),
                            ["status"] = command.Status
                        }),
                    cancellationToken);
            }
            catch (Exception notificationException)
            {
                logger.LogWarning(
                    notificationException,
                    "Failed to publish batch status notification for batch {BatchEntryId}",
                    command.BatchEntryId);
            }

            return Result.Success;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            logger.LogError(ex, "Timeout updating batch {BatchEntryId} in SAP Service Layer", command.BatchEntryId);
            return Errors.Batch.SapTimeout;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Network error updating batch {BatchEntryId} in SAP Service Layer", command.BatchEntryId);
            return Errors.Batch.SapConnectionError(ex.Message);
        }
        catch (Exception ex) when (ex.Message.Contains("404", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(ex, "Batch {BatchEntryId} was not found while updating status", command.BatchEntryId);
            return Errors.Batch.NotFound(command.BatchEntryId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error updating batch {BatchEntryId}", command.BatchEntryId);
            return Errors.Batch.UpdateFailed("Failed to update batch status.");
        }
    }
}