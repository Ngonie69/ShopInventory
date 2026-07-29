using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.WhatsApp.Commands.ReceiveOpenWAWebhook;

public sealed record ReceiveOpenWAWebhookCommand(
    string RawPayload,
    string? ProvidedSignature,
    string? ProvidedEventType,
    string? ProvidedIdempotencyKey,
    string? ProvidedDeliveryId,
    string? SourcePath) : IRequest<ErrorOr<WhatsAppWebhookReceiptDto>>;