using ShopInventory.DTOs;

namespace ShopInventory.Services;

/// <summary>
/// Keeps OpenWA pointed at this API's inbound webhook.
/// </summary>
/// <remarks>
/// A paired session delivers nothing on its own: OpenWA only posts events to webhooks that have
/// been registered against that session. Without this the console shows a connected session and
/// an inbox that stays permanently empty, which reads like a broken query rather than a missing
/// registration.
/// </remarks>
public interface IOpenWAWebhookRegistrar
{
    /// <summary>
    /// Registers or repairs the webhook for one session. Never throws for a gateway or
    /// configuration fault: the failure is reported in the returned status so the caller can
    /// surface it without losing the work it had already done.
    /// </summary>
    Task<WhatsAppWebhookStatusDto> EnsureAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports the webhook for one session without changing anything.
    /// </summary>
    Task<WhatsAppWebhookStatusDto> GetStatusAsync(string sessionId, CancellationToken cancellationToken = default);
}
