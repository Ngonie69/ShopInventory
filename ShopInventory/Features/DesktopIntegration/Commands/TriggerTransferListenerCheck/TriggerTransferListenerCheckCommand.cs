using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DesktopIntegration.Commands.TriggerTransferListenerCheck;

/// <summary>
/// Makes TransferEventListener poll SAP now rather than waiting for its next cycle.
/// </summary>
/// <remarks>
/// A command, not a query, and gated as one: it advances the listener's poll window, marks documents
/// processed and sends anything new it finds to this API's transfer-event webhook. It is safe to run twice — the second
/// pass finds those documents already processed — but it is an operator's lever, used when the poll
/// has been failing and someone wants the backlog applied without waiting five minutes.
/// </remarks>
public sealed record TriggerTransferListenerCheckCommand : IRequest<ErrorOr<TriggerTransferListenerCheckResult>>;

public sealed record TriggerTransferListenerCheckResult(
    DateTime CheckedAt,

    // Documents SAP returned for the window, monitored or not.
    int TotalSapTransfers,

    // Lines among those that touched a monitored warehouse and had not been seen before.
    int MonitoredEventsDetected,

    // Whether the check sent this API a new line, and whether this API took all of them. The counts
    // below say the same per line.
    bool WebhookTriggered,
    bool WebhookSuccess,

    // What happened to the lines on their way to this API's ledger during the check. All zero from
    // a listener older than its retry queue, which reports none of them.
    int NotificationsDelivered,
    int NotificationsQueued,
    int NotificationsReplayed,
    int NotificationsRejected,
    int NotificationsAbandoned,

    // Lines still waiting after the check.
    int PendingNotifications,

    string Message
);
