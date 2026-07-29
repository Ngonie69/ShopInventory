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
}