using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DesktopIntegration.Commands.TriggerTransferListenerCheck;

/// <summary>
/// Makes TransferEventListener poll SAP now rather than waiting for its next cycle.
/// </summary>
/// <remarks>
/// A command, not a query, and gated as one: it advances the listener's poll window, marks documents
/// processed and delivers webhooks for anything new it finds. It is safe to run twice — the second
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

    bool WebhookTriggered,
    bool WebhookSuccess,
    string Message
);
