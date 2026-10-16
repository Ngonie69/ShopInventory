using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ShopInventory.Web.Services;

/// <summary>
/// The text the inbox shows for a message's raw payload: the stored body, with any long string
/// value replaced by a note of its length.
/// </summary>
/// <remarks>
/// A received attachment arrives base64 in <c>data.media.data</c> and is kept whole, up to the
/// webhook's 20 MB. Nobody reads base64, and 27 MB of it in a <c>&lt;pre&gt;</c> would go through
/// the circuit and into the DOM, so it is cut here, after one read of the body. This walks the
/// tokens with <see cref="Utf8JsonReader"/> rather than building a JsonDocument, whose pooled
/// buffers stay with the process (see the w3wp memory notes). The API's
/// <c>GET /api/whatsapp/messages/{id}/raw</c> still has the body whole.
/// </remarks>
public static class WhatsAppRawPayloadView
{
    /// <summary>Longest string value shown as it is. A WhatsApp caption or text fits easily.</summary>
    public const int MaxShownStringLength = 2048;

    /// <summary>How much of a body that is not JSON is shown.</summary>
    public const int MaxShownTextLength = 64 * 1024;

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        SkipValidation = true
    };

    public static string Render(ReadOnlySpan<byte> utf8Body)
    {
        try
        {
            return RenderJson(utf8Body);
        }
        catch (JsonException)
        {
            var text = Encoding.UTF8.GetString(utf8Body);
            return text.Length <= MaxShownTextLength
                ? text
                : $"{text[..MaxShownTextLength]}… [{text.Length - MaxShownTextLength:N0} more characters not shown]";
        }
    }

    private static string RenderJson(ReadOnlySpan<byte> utf8Body)
    {
        var reader = new Utf8JsonReader(utf8Body, new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });

        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output, WriterOptions))
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
                        // The escaped length, which for base64 is the length: the cut is decided
                        // without decoding a 27 MB value into a string first.
                        writer.WriteStringValue(reader.ValueSpan.Length > MaxShownStringLength
                            ? $"[{reader.ValueSpan.Length:N0} characters not shown]"
                            : reader.GetString());
                        break;
                    case JsonTokenType.Number:
                        writer.WriteRawValue(reader.ValueSpan, skipInputValidation: true);
                        break;
                    case JsonTokenType.True:
                    case JsonTokenType.False:
                        writer.WriteBooleanValue(reader.GetBoolean());
                        break;
                    case JsonTokenType.Null:
                        writer.WriteNullValue();
                        break;
                }
            }
        }

        return Encoding.UTF8.GetString(output.WrittenSpan);
    }
}
