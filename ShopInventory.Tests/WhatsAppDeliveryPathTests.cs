using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Guards the three things that stood between a configured WhatsApp gateway and a working one.
/// </summary>
/// <remarks>
/// <para>
/// Each of these failed silently in its own way, which is why they are pinned by literal payloads
/// captured from the running pair rather than by hand-written approximations.
/// </para>
/// <para>
/// The registration one is the load-bearing case: a session with no webhook looks healthy from
/// every angle the console offers and captures nothing at all.
/// </para>
/// </remarks>
public class WhatsAppDeliveryPathTests
{
    /// <summary>
    /// Captured verbatim from POST /api/whatsapp/sessions with OpenWA:Enabled false.
    /// </summary>
    /// <remarks>
    /// Note the two "errors" keys. ApiControllerBase builds a ValidationProblemDetails, which
    /// serializes its own Errors dictionary, and then writes an Extensions["errors"] array beside
    /// it. Both reach the wire. JsonDocument keeps the first, so the dictionary is what a reader
    /// sees - but the duplicate is real and this constant is the evidence for it.
    /// </remarks>
    private const string DisabledGatewayProblem = """
        {"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"detail":"The request contains validation errors.","instance":"/api/whatsapp/sessions","errors":{"WhatsApp.Disabled":["WhatsApp integration is disabled"]},"code":"WhatsApp.Disabled","errors":[{"code":"WhatsApp.Disabled","description":"WhatsApp integration is disabled","type":"Validation"}],"traceId":"00-227e211d9ce25df23bbb5d32b07146cb-0110cc4c762a2dde-00"}
        """;

    /// <summary>
    /// Captured verbatim from OpenWA when a create body carried a property its DTO does not declare.
    /// </summary>
    private const string NestValidationProblem =
        """{"message":["property active should not exist"],"error":"Bad Request","statusCode":400}""";

    [Fact]
    public void A_disabled_gateway_reports_why_rather_than_the_frameworks_validation_title()
    {
        var message = ApiErrorResponse.GetFriendlyMessage(
            HttpStatusCode.BadRequest,
            DisabledGatewayProblem,
            "WhatsApp request failed.");

        // The console used to show the title, so every configuration fault - disabled integration,
        // missing API key, missing base URL - read as "One or more validation errors occurred."
        // against a request that had nothing wrong with it.
        Assert.Contains("WhatsApp integration is disabled", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("One or more validation errors", message, StringComparison.OrdinalIgnoreCase);

        // Just the sentence. The array form of "errors" holds {code, description, type} objects,
        // and taking every string out of one rendered
        // "WhatsApp.Disabled; WhatsApp integration is disabled; Validation." on the console.
        Assert.DoesNotContain("WhatsApp.Disabled", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Validation", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rejected_gateway_call_reports_what_OpenWA_objected_to()
    {
        var exception = new OpenWAGatewayException(HttpStatusCode.BadRequest, "Bad Request", NestValidationProblem);

        // Nest puts the detail in an ARRAY under "message" and the status label in "error". Reading
        // "message" as a string alone skipped it and surfaced "Bad Request", which is the whole
        // status code restated and told an operator nothing.
        Assert.Contains("property active should not exist", exception.Message, StringComparison.Ordinal);
        Assert.NotEqual("Bad Request", exception.Message);
    }

    [Fact]
    public async Task Registering_a_new_webhook_omits_the_property_OpenWAs_create_DTO_rejects()
    {
        var client = new RecordingOpenWAClient();
        var registrar = BuildRegistrar(client);

        var status = await registrar.EnsureAsync("session-1");

        Assert.True(status.Registered);
        Assert.Equal("created", status.Action);

        var created = Assert.Single(client.Created);
        // OpenWA runs its validation pipe with forbidNonWhitelisted, and CreateWebhookDto has no
        // "active". Sending it - even as null - makes the create a 400 and leaves the session with
        // no delivery path at all.
        Assert.False(SerializedBody(created).TryGetProperty("active", out _));
        Assert.Equal("http://10.10.10.9:5106/api/whatsapp/webhook/openwa", created.Url);
        Assert.Contains("message.received", created.Events);
    }

    [Fact]
    public async Task Repairing_an_existing_webhook_rewrites_the_secret_and_reactivates_it()
    {
        var client = new RecordingOpenWAClient
        {
            Existing =
            [
                new WhatsAppWebhookRegistrationDto
                {
                    Id = "hook-1",
                    SessionId = "session-1",
                    Url = "http://10.10.10.9:5106/api/whatsapp/webhook/openwa",
                    Events = ["message.received"],
                    Active = false
                }
            ]
        };

        var registrar = BuildRegistrar(client);
        var status = await registrar.EnsureAsync("session-1");

        Assert.Equal("updated", status.Action);
        Assert.Empty(client.Created);

        var (webhookId, updated) = Assert.Single(client.Updated);
        Assert.Equal("hook-1", webhookId);
        // OpenWA never returns the stored secret, so a secret that has drifted from
        // OpenWA:WebhookSecret cannot be detected - only overwritten. Left alone, every delivery
        // would fail signature verification while the console showed a registered webhook.
        Assert.Equal("the-shared-secret", updated.Secret);
        Assert.True(updated.Active);
    }

    [Fact]
    public async Task A_localhost_webhook_url_is_refused_before_OpenWA_can_reject_it()
    {
        var client = new RecordingOpenWAClient();
        var registrar = BuildRegistrar(client, webhookUrl: "http://localhost:5106/api/whatsapp/webhook/openwa");

        var status = await registrar.EnsureAsync("session-1");

        Assert.False(status.Registered);
        Assert.Equal("failed", status.Action);
        // OpenWA validates the URL with class-validator's IsUrl, which rejects the bare hostname
        // "localhost". Saying so here names the fix; letting OpenWA answer 400 does not.
        Assert.Contains("localhost", status.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(client.Created);
    }

    [Fact]
    public async Task A_gateway_that_will_not_answer_reports_the_fault_instead_of_throwing()
    {
        var client = new RecordingOpenWAClient
        {
            ListFailure = new HttpRequestException("Connection refused")
        };

        var status = await BuildRegistrar(client).EnsureAsync("session-1");

        // The caller has already created or started the session by this point. Throwing here would
        // report work that succeeded as having failed, and send an operator to undo it.
        Assert.False(status.Registered);
        Assert.Equal("failed", status.Action);
        Assert.Contains("Connection refused", status.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Round-trips the request through the serializer the client actually uses, so the assertion is
    /// about the JSON that reaches OpenWA rather than about the C# object.
    /// </summary>
    private static JsonElement SerializedBody(WhatsAppWebhookRegistrationRequestDto request)
    {
        var json = JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return JsonDocument.Parse(json).RootElement;
    }

    private static OpenWAWebhookRegistrar BuildRegistrar(
        IOpenWAClient client,
        string webhookUrl = "http://10.10.10.9:5106/api/whatsapp/webhook/openwa")
    {
        var settings = new OpenWASettings
        {
            Enabled = true,
            BaseUrl = "http://10.10.10.9:2785",
            ApiKey = "owa_k1_test",
            WebhookSecret = "the-shared-secret",
            WebhookPublicUrl = webhookUrl
        };

        return new OpenWAWebhookRegistrar(
            client,
            Options.Create(settings),
            NullLogger<OpenWAWebhookRegistrar>.Instance);
    }

    private sealed class RecordingOpenWAClient : IOpenWAClient
    {
        public List<WhatsAppWebhookRegistrationDto> Existing { get; init; } = [];

        public Exception? ListFailure { get; init; }

        public List<WhatsAppWebhookRegistrationRequestDto> Created { get; } = [];

        public List<(string WebhookId, WhatsAppWebhookRegistrationRequestDto Request)> Updated { get; } = [];

        public Task<List<WhatsAppWebhookRegistrationDto>> GetSessionWebhooksAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            return ListFailure is not null
                ? Task.FromException<List<WhatsAppWebhookRegistrationDto>>(ListFailure)
                : Task.FromResult(Existing);
        }

        public Task<WhatsAppWebhookRegistrationDto> CreateSessionWebhookAsync(string sessionId, WhatsAppWebhookRegistrationRequestDto request, CancellationToken cancellationToken = default)
        {
            Created.Add(request);

            return Task.FromResult(new WhatsAppWebhookRegistrationDto
            {
                Id = "hook-new",
                SessionId = sessionId,
                Url = request.Url,
                Events = request.Events,
                Active = true
            });
        }

        public Task<WhatsAppWebhookRegistrationDto> UpdateSessionWebhookAsync(string sessionId, string webhookId, WhatsAppWebhookRegistrationRequestDto request, CancellationToken cancellationToken = default)
        {
            Updated.Add((webhookId, request));

            return Task.FromResult(new WhatsAppWebhookRegistrationDto
            {
                Id = webhookId,
                SessionId = sessionId,
                Url = request.Url,
                Events = request.Events,
                Active = request.Active ?? true
            });
        }

        public Task<WhatsAppHealthDto?> GetHealthAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WhatsAppSessionDto> CreateSessionAsync(WhatsAppCreateSessionRequestDto request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<List<WhatsAppSessionDto>> GetSessionsAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WhatsAppSessionDto> StartSessionAsync(string sessionId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WhatsAppSessionDto> StopSessionAsync(string sessionId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WhatsAppQrCodeDto> GetSessionQrCodeAsync(string sessionId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WhatsAppMessageDispatchDto> SendTextAsync(string sessionId, WhatsAppSendTextRequestDto request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WhatsAppMessageDispatchDto> ReplyAsync(string sessionId, WhatsAppReplyRequestDto request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
