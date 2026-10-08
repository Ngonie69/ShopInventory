using System.Text.Json.Serialization;

namespace ShopInventory.DTOs;

/// <summary>
/// The body of OpenWA's <c>POST /api/sessions/{id}/messages/send-document</c>.
/// </summary>
/// <remarks>
/// Exactly the properties OpenWA's <c>SendMediaMessageDto</c> declares and no others: the gateway runs
/// its validation with <c>forbidNonWhitelisted</c>, so one extra property — even a null one — refuses
/// the whole send with a 400. That is how the webhook registration once broke on <c>active</c>.
/// There is deliberately no <c>url</c>: the file always travels in the body, so nothing has to serve it
/// to the gateway, and the gateway's URL path ignores the file name.
/// </remarks>
public sealed class WhatsAppSendDocumentRequestDto
{
    /// <summary>The recipient, <c>2637XXXXXXXX@c.us</c>.</summary>
    public string ChatId { get; set; } = string.Empty;

    /// <summary>The file, base64-encoded with no <c>data:</c> prefix.</summary>
    public string Base64 { get; set; } = string.Empty;

    /// <summary>The file's media type; OpenWA requires it whenever the file travels as base64.</summary>
    public string Mimetype { get; set; } = "application/pdf";

    /// <summary>The name the recipient sees on the document.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Filename { get; set; }

    /// <summary>The message shown under the document, at most 1024 characters.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Caption { get; set; }
}
