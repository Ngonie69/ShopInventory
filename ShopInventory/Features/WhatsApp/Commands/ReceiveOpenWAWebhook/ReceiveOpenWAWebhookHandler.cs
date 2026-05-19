using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.WhatsApp.Commands.ReceiveOpenWAWebhook;

public sealed class ReceiveOpenWAWebhookHandler(
    ApplicationDbContext dbContext,
    IOptions<OpenWASettings> settings,
    ILogger<ReceiveOpenWAWebhookHandler> logger) : IRequestHandler<ReceiveOpenWAWebhookCommand, ErrorOr<WhatsAppWebhookReceiptDto>>
{
    private readonly ApplicationDbContext _dbContext = dbContext;
    private readonly OpenWASettings _settings = settings.Value;
    private readonly ILogger<ReceiveOpenWAWebhookHandler> _logger = logger;

    public async Task<ErrorOr<WhatsAppWebhookReceiptDto>> Handle(
        ReceiveOpenWAWebhookCommand command,
        CancellationToken cancellationToken)
    {
        if (!_settings.Enabled)
        {
            return Errors.WhatsApp.Disabled;
        }

        if (string.IsNullOrWhiteSpace(command.RawPayload))
        {
            return Errors.WhatsApp.InvalidPayload;
        }

        if (string.IsNullOrWhiteSpace(_settings.WebhookSecret) || _settings.WebhookSecret.StartsWith("${", StringComparison.Ordinal))
        {
            return Errors.WhatsApp.MissingWebhookSecretConfiguration;
        }

        if (!SignatureMatches(_settings.WebhookSecret, command.RawPayload, command.ProvidedSignature))
        {
            return Errors.WhatsApp.InvalidWebhookSignature;
        }

        if (!string.IsNullOrWhiteSpace(command.ProvidedIdempotencyKey))
        {
            var existing = await _dbContext.WhatsAppWebhookEvents
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    message => message.IdempotencyKey == command.ProvidedIdempotencyKey,
                    cancellationToken);

            if (existing is not null)
            {
                return new WhatsAppWebhookReceiptDto
                {
                    Id = existing.Id,
                    EventType = existing.EventType,
                    ReceivedAtUtc = existing.ReceivedAtUtc
                };
            }
        }

        var entity = ParsePayload(command.RawPayload, command.ProvidedEventType, command.SourcePath);
        entity.IdempotencyKey = command.ProvidedIdempotencyKey;
        entity.DeliveryId = command.ProvidedDeliveryId;
        _dbContext.WhatsAppWebhookEvents.Add(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Stored OpenWA webhook event {EventType} for sender {SenderNumber} at {ReceivedAtUtc}",
            entity.EventType,
            entity.SenderNumber,
            entity.ReceivedAtUtc);

        return new WhatsAppWebhookReceiptDto
        {
            Id = entity.Id,
            EventType = entity.EventType,
            ReceivedAtUtc = entity.ReceivedAtUtc
        };
    }

    private WhatsAppWebhookEventEntity ParsePayload(string rawPayload, string? providedEventType, string? sourcePath)
    {
        try
        {
            using var jsonDocument = JsonDocument.Parse(rawPayload);
            var root = jsonDocument.RootElement;
            var payload = root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                ? data
                : root;

            var isFromMe = ReadBool(payload, "fromMe")
                ?? ReadNestedBool(payload, "id", "fromMe")
                ?? ReadBool(root, "fromMe")
                ?? false;

            return new WhatsAppWebhookEventEntity
            {
                EventType = FirstNonEmpty(
                    ReadString(root, "event"),
                    ReadString(root, "type"),
                    providedEventType,
                    "unknown")!,
                SessionName = FirstNonEmpty(
                    ReadString(root, "sessionId"),
                    ReadString(root, "session"),
                    ReadString(root, "sessionName"),
                    ReadString(payload, "sessionId"),
                    ReadString(payload, "session")),
                MessageId = FirstNonEmpty(
                    ReadString(payload, "id"),
                    ReadNestedString(payload, "id", "_serialized"),
                    ReadNestedString(payload, "id", "id"),
                    ReadString(root, "id")),
                ChatId = FirstNonEmpty(
                    ReadString(payload, "chatId"),
                    ReadNestedString(payload, "id", "remote"),
                    ReadString(payload, "from"),
                    ReadString(payload, "to")),
                SenderNumber = FirstNonEmpty(
                    ReadString(payload, "from"),
                    ReadString(payload, "author"),
                    ReadNestedString(payload, "sender", "id"),
                    ReadNestedString(payload, "chat", "contact", "id"),
                    ReadNestedString(payload, "id", "remote")),
                SenderDisplayName = FirstNonEmpty(
                    ReadNestedString(payload, "sender", "pushname"),
                    ReadNestedString(payload, "sender", "name"),
                    ReadNestedString(payload, "chat", "contact", "pushname"),
                    ReadString(payload, "notifyName"),
                    ReadString(payload, "senderName")),
                MessageType = FirstNonEmpty(
                    ReadString(payload, "type"),
                    ReadString(root, "type")),
                Direction = isFromMe ? "outbound" : "inbound",
                Status = ReadStatus(payload, root),
                IsFromMe = isFromMe,
                TextBody = FirstNonEmpty(
                    ReadString(payload, "body"),
                    ReadString(payload, "text"),
                    ReadNestedString(payload, "text", "body"),
                    ReadString(root, "body")),
                SourcePath = sourcePath,
                OccurredAtUtc = ReadDateTime(payload, "timestamp")
                    ?? ReadDateTime(payload, "t")
                    ?? ReadDateTime(root, "timestamp"),
                ReceivedAtUtc = DateTime.UtcNow,
                RawPayload = rawPayload
            };
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "OpenWA webhook payload was not valid JSON");

            return new WhatsAppWebhookEventEntity
            {
                EventType = FirstNonEmpty(providedEventType, "unknown")!,
                Direction = "unknown",
                SourcePath = sourcePath,
                ReceivedAtUtc = DateTime.UtcNow,
                RawPayload = rawPayload
            };
        }
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.ToString(),
            JsonValueKind.True => bool.TrueString.ToLowerInvariant(),
            JsonValueKind.False => bool.FalseString.ToLowerInvariant(),
            _ => null
        };
    }

    private static string? ReadNestedString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.ToString(),
            JsonValueKind.True => bool.TrueString.ToLowerInvariant(),
            JsonValueKind.False => bool.FalseString.ToLowerInvariant(),
            _ => null
        };
    }

    private static bool? ReadBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(property.GetString(), out var value) => value,
            JsonValueKind.Number when property.TryGetInt32(out var number) => number != 0,
            _ => null
        };
    }

    private static bool? ReadNestedBool(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(current.GetString(), out var value) => value,
            JsonValueKind.Number when current.TryGetInt32(out var number) => number != 0,
            _ => null
        };
    }

    private static DateTime? ReadDateTime(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.String && DateTime.TryParse(
                property.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsedDateTime))
        {
            return parsedDateTime;
        }

        if (property.ValueKind == JsonValueKind.Number)
        {
            if (property.TryGetInt64(out var epoch))
            {
                return epoch > 100000000000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(epoch).UtcDateTime
                    : DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;
            }

            if (property.TryGetDouble(out var numericEpoch))
            {
                var wholeEpoch = Convert.ToInt64(numericEpoch);
                return wholeEpoch > 100000000000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(wholeEpoch).UtcDateTime
                    : DateTimeOffset.FromUnixTimeSeconds(wholeEpoch).UtcDateTime;
            }
        }

        return null;
    }

    private static string? ReadStatus(JsonElement payload, JsonElement root)
    {
        return FirstNonEmpty(
            ReadString(payload, "status"),
            ReadString(payload, "ack"),
            ReadString(root, "status"));
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static bool SignatureMatches(string configuredSecret, string payload, string? providedSignature)
    {
        if (string.IsNullOrWhiteSpace(providedSignature))
        {
            return false;
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(configuredSecret.Trim()));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        var expectedSignature = $"sha256={Convert.ToHexString(hash).ToLowerInvariant()}";

        var expectedBytes = Encoding.UTF8.GetBytes(expectedSignature);
        var providedBytes = Encoding.UTF8.GetBytes(providedSignature.Trim().ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }
}