namespace ShopInventory.Features.CustomerDocuments;

/// <summary>
/// The half of the customer-document settings an administrator changes while the system runs.
/// </summary>
/// <param name="AutoSendEnabled">Whether invoices are queued and sent without anyone pressing Send.</param>
/// <param name="WhatsAppSessionId">
/// The OpenWA session documents are sent from; null until one is chosen — by an administrator, or by
/// <see cref="CustomerDocumentSession"/> the first time something needs sending.
/// </param>
/// <param name="SendingStopped">
/// An administrator stopped all sending. While it is set no session is used and none is chosen
/// automatically.
/// </param>
/// <param name="MaxAutoPerDay">The most automatic sends in a CAT day — the warm-up cap.</param>
/// <param name="ChangedBy">Who last saved these.</param>
/// <param name="ChangedAtUtc">When they did.</param>
public sealed record CustomerDocumentRuntimeSettings(
    bool AutoSendEnabled,
    string? WhatsAppSessionId,
    bool SendingStopped,
    int MaxAutoPerDay,
    string? ChangedBy,
    DateTime? ChangedAtUtc);
