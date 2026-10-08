using ShopInventory.DTOs;

namespace ShopInventory.Services;

/// <summary>
/// Transport-only client for the OpenWA gateway.
/// </summary>
public interface IOpenWAClient
{
    Task<WhatsAppHealthDto?> GetHealthAsync(CancellationToken cancellationToken = default);
    Task<WhatsAppSessionDto> CreateSessionAsync(WhatsAppCreateSessionRequestDto request, CancellationToken cancellationToken = default);
    Task<List<WhatsAppSessionDto>> GetSessionsAsync(CancellationToken cancellationToken = default);
    Task<WhatsAppSessionDto> StartSessionAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<WhatsAppSessionDto> StopSessionAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<WhatsAppQrCodeDto> GetSessionQrCodeAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<WhatsAppMessageDispatchDto> SendTextAsync(string sessionId, WhatsAppSendTextRequestDto request, CancellationToken cancellationToken = default);
    Task<WhatsAppMessageDispatchDto> ReplyAsync(string sessionId, WhatsAppReplyRequestDto request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a file as a WhatsApp document. Bounded by <c>OpenWA:DocumentTimeoutSeconds</c> rather than
    /// the ordinary timeout, because the gateway uploads the file to WhatsApp before it answers.
    /// </summary>
    Task<WhatsAppMessageDispatchDto> SendDocumentAsync(string sessionId, WhatsAppSendDocumentRequestDto request, CancellationToken cancellationToken = default);

    /// <summary>Asks whether <paramref name="digits"/> (country code and number, digits only) has a WhatsApp account.</summary>
    Task<WhatsAppNumberCheckDto> CheckNumberAsync(string sessionId, string digits, CancellationToken cancellationToken = default);

    /// <summary>A page of the gateway's own message log for one chat, newest first.</summary>
    Task<WhatsAppMessageHistoryDto> GetMessagesAsync(string sessionId, string chatId, int limit, CancellationToken cancellationToken = default);

    Task<List<WhatsAppWebhookRegistrationDto>> GetSessionWebhooksAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<WhatsAppWebhookRegistrationDto> CreateSessionWebhookAsync(string sessionId, WhatsAppWebhookRegistrationRequestDto request, CancellationToken cancellationToken = default);
    Task<WhatsAppWebhookRegistrationDto> UpdateSessionWebhookAsync(string sessionId, string webhookId, WhatsAppWebhookRegistrationRequestDto request, CancellationToken cancellationToken = default);
}
