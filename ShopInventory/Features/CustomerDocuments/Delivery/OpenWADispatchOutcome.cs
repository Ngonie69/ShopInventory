namespace ShopInventory.Features.CustomerDocuments.Delivery;

/// <summary>A classified send outcome.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="MessageId">WhatsApp's id for the message, when it gave one.</param>
/// <param name="GatewayTimestamp">The gateway's timestamp for the send.</param>
/// <param name="Error">The failure, in words, when there was one.</param>
/// <param name="AlertCondition">The administrator alert this outcome warrants, if any.</param>
public sealed record OpenWADispatchOutcome(
    OpenWADispatchOutcomeKind Kind,
    string? MessageId,
    long? GatewayTimestamp,
    string? Error,
    string? AlertCondition = null);
