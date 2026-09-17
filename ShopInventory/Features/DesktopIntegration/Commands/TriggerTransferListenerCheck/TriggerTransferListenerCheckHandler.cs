using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Commands.TriggerTransferListenerCheck;

public sealed class TriggerTransferListenerCheckHandler(
    ITransferEventListenerClient listenerClient,
    ILogger<TriggerTransferListenerCheckHandler> logger
) : IRequestHandler<TriggerTransferListenerCheckCommand, ErrorOr<TriggerTransferListenerCheckResult>>
{
    public async Task<ErrorOr<TriggerTransferListenerCheckResult>> Handle(
        TriggerTransferListenerCheckCommand request,
        CancellationToken cancellationToken)
    {
        if (!listenerClient.IsEnabled)
        {
            return Errors.DesktopIntegration.TransferListenerDisabled();
        }

        try
        {
            var result = await listenerClient.TriggerCheckAsync(cancellationToken);

            logger.LogInformation(
                "TransferEventListener check-now: {TotalTransfers} SAP transfer(s), {Detected} monitored line(s), "
                + "{Delivered} delivered to the ledger, {Queued} queued, {Pending} still waiting",
                result.TotalSapTransfers, result.MonitoredEventsDetected,
                result.NotificationsDelivered, result.NotificationsQueued, result.PendingNotifications);

            return new TriggerTransferListenerCheckResult(
                result.CheckedAt,
                result.TotalSapTransfers,
                result.MonitoredEventsDetected,
                result.WebhookTriggered,
                result.WebhookSuccess,
                result.NotificationsDelivered,
                result.NotificationsQueued,
                result.NotificationsReplayed,
                result.NotificationsRejected,
                result.NotificationsAbandoned,
                result.PendingNotifications,
                result.Message ?? string.Empty);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "TransferEventListener check-now failed against {BaseUrl}", listenerClient.BaseUrl);
            return Errors.DesktopIntegration.TransferListenerUnreachable(listenerClient.BaseUrl, ex.Message);
        }
    }
}
