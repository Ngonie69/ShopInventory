using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Idempotency;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.WhatsApp.Commands.ReceiveOpenWAWebhook;

public sealed class ReceiveOpenWAWebhookHandler(
    ApplicationDbContext dbContext,
    IOptions<OpenWASettings> settings,
    IIdempotencyRequestStore idempotencyRequestStore,
    ILogger<ReceiveOpenWAWebhookHandler> logger) : IRequestHandler<ReceiveOpenWAWebhookCommand, ErrorOr<WhatsAppWebhookReceiptDto>>
{
    private readonly ApplicationDbContext _dbContext = dbContext;
    private readonly OpenWASettings _settings = settings.Value;
    private readonly IIdempotencyRequestStore _idempotencyRequestStore = idempotencyRequestStore;
    private readonly ILogger<ReceiveOpenWAWebhookHandler> _logger = logger;

    public async Task<ErrorOr<WhatsAppWebhookReceiptDto>> Handle(
        ReceiveOpenWAWebhookCommand command,
        CancellationToken cancellationToken)
    {
        long? idempotencyRequestId = null;
        var releaseIdempotencyRequest = false;

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

        var utf8Payload = Encoding.UTF8.GetBytes(command.RawPayload);

        if (!SignatureMatches(_settings.WebhookSecret, utf8Payload, command.ProvidedSignature))
        {
            return Errors.WhatsApp.InvalidWebhookSignature;
        }

        var entity = ParsePayload(command.RawPayload, utf8Payload, command.ProvidedEventType, command.SourcePath, out var isStatusPost);
        var normalizedIdempotencyKey = DeduplicationKey(entity, command.ProvidedDeliveryId, command.ProvidedIdempotencyKey);

        try
        {
            if (!string.IsNullOrWhiteSpace(normalizedIdempotencyKey))
            {
                // Fingerprinted on the key alone. The body differs on every dispatch (its timestamp
                // and delivery id), so a body fingerprint answered a second dispatch under the same
                // key 409, which OpenWA retries three times and then drops.
                var acquireResult = await _idempotencyRequestStore.TryAcquireAsync<WhatsAppWebhookReceiptDto>(
                    "whatsapp.webhook.receive",
                    normalizedIdempotencyKey,
                    new { DeduplicationKey = normalizedIdempotencyKey },
                    cancellationToken);

                switch (acquireResult.Outcome)
                {
                    case IdempotencyAcquireOutcome.ReplayAvailable when acquireResult.Response is not null:
                        return acquireResult.Response;
                    case IdempotencyAcquireOutcome.InProgress:
                        return Errors.Idempotency.RequestInProgress("WhatsApp webhook receipt");
                    case IdempotencyAcquireOutcome.RequestMismatch:
                        return Errors.Idempotency.RequestMismatch("WhatsApp webhook receipt");
                    case IdempotencyAcquireOutcome.Acquired:
                        idempotencyRequestId = acquireResult.RequestId;
                        releaseIdempotencyRequest = true;
                        break;
                }
            }

            if (!string.IsNullOrWhiteSpace(normalizedIdempotencyKey))
            {
                // Projected, so a redelivered attachment does not read its stored payload back.
                var existingResponse = await _dbContext.WhatsAppWebhookEvents
                    .AsNoTracking()
                    .Where(message => message.IdempotencyKey == normalizedIdempotencyKey)
                    .Select(message => new WhatsAppWebhookReceiptDto
                    {
                        Id = message.Id,
                        EventType = message.EventType,
                        ReceivedAtUtc = message.ReceivedAtUtc
                    })
                    .FirstOrDefaultAsync(cancellationToken);

                if (existingResponse is not null)
                {
                    if (idempotencyRequestId.HasValue)
                    {
                        try
                        {
                            await _idempotencyRequestStore.CompleteAsync(idempotencyRequestId.Value, existingResponse, cancellationToken);
                            releaseIdempotencyRequest = false;
                        }
                        catch (Exception completeException)
                        {
                            _logger.LogWarning(completeException, "Failed to persist WhatsApp webhook idempotency replay for request {RequestId}", idempotencyRequestId.Value);
                        }
                    }

                    return existingResponse;
                }
            }

            entity.IdempotencyKey = normalizedIdempotencyKey;
            entity.DeliveryId = command.ProvidedDeliveryId;

            // A contact's WhatsApp Status post reaches the session as an ordinary message on
            // status@broadcast. Nobody sent it to us, so it is acknowledged and not kept: kept, it
            // showed in the inbox as a "+status" thread full of other people's adverts.
            if (isStatusPost)
            {
                _logger.LogDebug(
                    "Dropped OpenWA webhook event {EventType}: a WhatsApp Status post, not a message",
                    entity.EventType);
            }
            else
            {
                _dbContext.WhatsAppWebhookEvents.Add(entity);
                await _dbContext.SaveChangesAsync(cancellationToken);

                _logger.LogInformation(
                    "Stored OpenWA webhook event {EventType} for sender {SenderNumber} at {ReceivedAtUtc}",
                    entity.EventType,
                    entity.SenderNumber,
                    entity.ReceivedAtUtc);
            }

            var response = new WhatsAppWebhookReceiptDto
            {
                Id = entity.Id,
                EventType = entity.EventType,
                ReceivedAtUtc = entity.ReceivedAtUtc
            };

            if (idempotencyRequestId.HasValue)
            {
                try
                {
                    await _idempotencyRequestStore.CompleteAsync(idempotencyRequestId.Value, response, cancellationToken);
                    releaseIdempotencyRequest = false;
                }
                catch (Exception completeException)
                {
                    _logger.LogWarning(completeException, "Failed to persist WhatsApp webhook idempotency completion for request {RequestId}", idempotencyRequestId.Value);
                }
            }

            return response;
        }
        finally
        {
            if (releaseIdempotencyRequest && idempotencyRequestId.HasValue)
            {
                try
                {
                    await _idempotencyRequestStore.ReleaseAsync(idempotencyRequestId.Value, CancellationToken.None);
                }
                catch (Exception releaseException)
                {
                    _logger.LogWarning(releaseException, "Failed to release WhatsApp webhook idempotency request {RequestId}", idempotencyRequestId.Value);
                }
            }
        }
    }

    private WhatsAppWebhookEventEntity ParsePayload(string rawPayload, byte[] utf8Payload, string? providedEventType, string? sourcePath, out bool isStatusPost)
    {
        isStatusPost = false;

        try
        {
            using var jsonDocument = JsonDocument.Parse(WithoutLongStrings(utf8Payload));
            var root = jsonDocument.RootElement;
            var payload = root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                ? data
                : root;

            isStatusPost = IsStatusPost(payload);

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

    /// <summary>
    /// What a receipt is deduplicated on. OpenWA's own <c>X-OpenWA-Idempotency-Key</c> is not used
    /// for messages: it reads <c>data.messageId</c>, which a received message does not carry, so
    /// every message on every session arrived keyed <c>msg_unknown</c>. The first one was stored and
    /// every later one was answered 409 (and dropped by OpenWA after three tries) or, once the claim
    /// had expired, answered 202 with the first message's receipt and never stored.
    /// </summary>
    /// <remarks>
    /// A message is identified by its session and WhatsApp message id, so a re-dispatch of the same
    /// message replays the first receipt. Anything else — session lifecycle events, or a message
    /// with no id — is one event per dispatch: OpenWA keeps the delivery id across its retries of a
    /// dispatch and mints a new one for the next, so a second <c>ready</c> is a new event and not a
    /// conflicting copy of the first. The sender's key is the last resort.
    /// </remarks>
    private static string? DeduplicationKey(WhatsAppWebhookEventEntity parsed, string? providedDeliveryId, string? providedIdempotencyKey)
    {
        string? key = null;

        if (MessageIdentityEvents.Contains(parsed.EventType) && !string.IsNullOrWhiteSpace(parsed.MessageId))
        {
            key = $"openwa:{parsed.EventType}:{parsed.SessionName?.Trim()}:{parsed.MessageId.Trim()}";
        }
        else if (!string.IsNullOrWhiteSpace(providedDeliveryId))
        {
            key = $"openwa:delivery:{providedDeliveryId.Trim()}";
        }
        else if (!string.IsNullOrWhiteSpace(providedIdempotencyKey))
        {
            key = providedIdempotencyKey.Trim();
        }

        // IdempotencyRequests holds 200 characters. A group message id names the group and the
        // author, so a long one is hashed rather than cut, which could merge two messages.
        if (key is { Length: > MaxDeduplicationKeyLength })
        {
            key = $"openwa:sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant()}";
        }

        return key;
    }

    private const int MaxDeduplicationKeyLength = 200;

    private static readonly HashSet<string> MessageIdentityEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        "message.received",
        "message.sent"
    };

    /// <summary>
    /// A Status post names <c>status@broadcast</c> as its chat. Which field carries it depends on
    /// direction: <c>from</c> for a contact's post, <c>to</c> for the session's own, and
    /// <c>chatId</c> / <c>id.remote</c> for either, so all four are checked as well as the
    /// <c>isStatus</c> flag.
    /// </summary>
    private static bool IsStatusPost(JsonElement payload)
    {
        if (ReadBool(payload, "isStatus") == true)
        {
            return true;
        }

        return new[]
            {
                ReadString(payload, "chatId"),
                ReadNestedString(payload, "id", "remote"),
                ReadString(payload, "from"),
                ReadString(payload, "to")
            }
            .Any(chatId => string.Equals(chatId?.Trim(), StatusBroadcastChatId, StringComparison.OrdinalIgnoreCase));
    }

    private const string StatusBroadcastChatId = "status@broadcast";

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

    /// <summary>
    /// The payload with every string longer than <see cref="LongStringBytes"/> emptied: in practice
    /// an attachment's base64, <c>data.media.data</c>, which runs to megabytes. The fields are read
    /// from this copy, so a parse costs what a text message's does. <see cref="JsonDocument"/> over
    /// the whole body rents pooled buffers the size of the body, and the shared pool keeps them.
    /// </summary>
    /// <remarks>
    /// A WhatsApp text is at most 65,536 characters, so no text body is long enough to be emptied.
    /// The stored <c>RawPayload</c> is the body as received, attachment included.
    /// </remarks>
    private static byte[] WithoutLongStrings(byte[] utf8Payload)
    {
        var reader = new Utf8JsonReader(utf8Payload);
        var copy = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(copy))
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        writer.WriteStartObject();
                        break;
                    case JsonTokenType.EndObject:
                        writer.WriteEndObject();
                        break;
                    case JsonTokenType.StartArray:
                        writer.WriteStartArray();
                        break;
                    case JsonTokenType.EndArray:
                        writer.WriteEndArray();
                        break;
                    case JsonTokenType.PropertyName:
                        writer.WritePropertyName(reader.GetString()!);
                        break;
                    case JsonTokenType.String:
                        writer.WriteStringValue(reader.ValueSpan.Length > LongStringBytes ? string.Empty : reader.GetString());
                        break;
                    case JsonTokenType.Number:
                    case JsonTokenType.True:
                    case JsonTokenType.False:
                    case JsonTokenType.Null:
                        writer.WriteRawValue(reader.ValueSpan, skipInputValidation: true);
                        break;
                }
            }
        }

        return copy.WrittenSpan.ToArray();
    }

    private const int LongStringBytes = 256 * 1024;

    private static bool SignatureMatches(string configuredSecret, byte[] utf8Payload, string? providedSignature)
    {
        if (string.IsNullOrWhiteSpace(providedSignature))
        {
            return false;
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(configuredSecret.Trim()));
        var hash = hmac.ComputeHash(utf8Payload);
        var expectedSignature = $"sha256={Convert.ToHexString(hash).ToLowerInvariant()}";

        var expectedBytes = Encoding.UTF8.GetBytes(expectedSignature);
        var providedBytes = Encoding.UTF8.GetBytes(providedSignature.Trim().ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }
}