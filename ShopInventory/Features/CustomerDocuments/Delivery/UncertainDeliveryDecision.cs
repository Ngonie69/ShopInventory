using ShopInventory.Models.Entities;

namespace ShopInventory.Features.CustomerDocuments.Delivery;

/// <summary>What an uncertain send turned out to be.</summary>
/// <param name="Status">Sent, SentUnconfirmed, Failed, or Pending to try again.</param>
/// <param name="MessageId">WhatsApp's id for it, when the log has one.</param>
/// <param name="Reason">What the log said, for the delivery's history.</param>
public sealed record UncertainDeliveryDecision(
    CustomerDocumentDeliveryStatus Status,
    string? MessageId,
    string Reason);
