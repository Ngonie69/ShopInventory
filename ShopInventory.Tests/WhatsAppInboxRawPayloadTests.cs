using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using MediatR;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShopInventory.Controllers;
using ShopInventory.Features.WhatsApp.Queries.GetWhatsAppInbox;
using ShopInventory.Features.WhatsApp.Queries.GetWhatsAppRawPayload;
using ShopInventory.Services;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The inbox list carries no attachment bytes, and one message's raw payload is still there when
/// asked for. Proved over HTTP against the real <see cref="WhatsAppController"/> and handlers, on a
/// row stored by the real webhook handler.
/// </summary>
/// <remarks>
/// #765 keeps a received attachment whole in <c>RawPayload</c>, base64 in <c>data.media.data</c>,
/// up to 20 MB. The list used to project <c>RawPayload</c> for every row, so a page of 50 could
/// carry tens of MB through the API, the Blazor circuit and the DOM.
/// </remarks>
public sealed class WhatsAppInboxRawPayloadTests : IDisposable
{
    private const string SessionId = "3270941b-0000-4000-8000-000000000002";

    private readonly WhatsAppWebhookHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task The_list_has_no_attachment_bytes_and_the_raw_route_has_them_all()
    {
        var (body, base64) = ImageMessage("false_263771234567@c.us_3EB0A1", rawImageBytes: 3_000_000, caption: "Proof of payment");
        Assert.False((await _harness.Receive(body)).IsError);
        await using var app = await StartAsync();
        using var http = Client(app, signedIn: true);

        using var list = await http.GetAsync("/api/whatsapp/messages?page=1&pageSize=200");
        var listJson = await list.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using (var document = JsonDocument.Parse(listJson))
        {
            var message = Assert.Single(document.RootElement.GetProperty("messages").EnumerateArray());
            Assert.Equal("Proof of payment", message.GetProperty("textBody").GetString());
            Assert.False(message.TryGetProperty("rawPayload", out _));
        }

        // Any 64 characters of the attachment would do; none may be in the list.
        Assert.DoesNotContain(base64.Substring(base64.Length / 2, 64), listJson, StringComparison.Ordinal);
        Assert.True(listJson.Length < 4096, $"The list answered {listJson.Length:N0} characters for one message.");

        var id = await StoredIdAsync();
        using var raw = await http.GetAsync($"/api/whatsapp/messages/{id}/raw");

        Assert.Equal(HttpStatusCode.OK, raw.StatusCode);
        Assert.Equal("application/json", raw.Content.Headers.ContentType?.MediaType);
        Assert.Equal(body, await raw.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_raw_route_answers_404_for_an_unknown_message()
    {
        await using var app = await StartAsync();
        using var http = Client(app, signedIn: true);

        using var raw = await http.GetAsync("/api/whatsapp/messages/4242/raw");

        Assert.Equal(HttpStatusCode.NotFound, raw.StatusCode);
    }

    [Fact]
    public async Task The_raw_route_is_admin_only()
    {
        var (body, _) = ImageMessage("false_263771234567@c.us_3EB0A2", rawImageBytes: 1_000, caption: "Hello");
        Assert.False((await _harness.Receive(body)).IsError);
        await using var app = await StartAsync();
        var id = await StoredIdAsync();

        using var anonymous = Client(app, signedIn: false);
        using var refused = await anonymous.GetAsync($"/api/whatsapp/messages/{id}/raw");
        using var nonAdmin = Client(app, signedIn: true, role: "Cashier");
        using var forbidden = await nonAdmin.GetAsync($"/api/whatsapp/messages/{id}/raw");

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task The_inbox_shows_the_payload_with_the_attachment_cut_and_the_rest_whole()
    {
        var (body, base64) = ImageMessage("false_263771234567@c.us_3EB0A3", rawImageBytes: 3_000_000, caption: "Proof \"of\" payment");
        Assert.False((await _harness.Receive(body)).IsError);
        await using var app = await StartAsync();
        using var http = Client(app, signedIn: true);
        var id = await StoredIdAsync();

        using var raw = await http.GetAsync($"/api/whatsapp/messages/{id}/raw");
        var view = WhatsAppRawPayloadView.Render(await raw.Content.ReadAsByteArrayAsync());

        Assert.DoesNotContain(base64.Substring(0, 64), view, StringComparison.Ordinal);
        Assert.Contains($"\"data\":\"[{base64.Length:N0} characters not shown]\"", view, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(view);
        var data = document.RootElement.GetProperty("data");
        Assert.Equal("Proof \"of\" payment", data.GetProperty("body").GetString());
        Assert.Equal("image/jpeg", data.GetProperty("media").GetProperty("mimetype").GetString());
        Assert.Equal(1760605200, data.GetProperty("timestamp").GetInt64());
        Assert.False(data.GetProperty("fromMe").GetBoolean());
        Assert.True(view.Length < 4096, $"The view is {view.Length:N0} characters.");
    }

    [Fact]
    public void A_payload_with_no_long_value_is_shown_unchanged()
    {
        const string body = """{"event":"message.ack","data":{"id":"x","ack":3,"note":null,"list":[1,2.5,true]}}""";

        Assert.Equal(body, WhatsAppRawPayloadView.Render(Encoding.UTF8.GetBytes(body)));
    }

    [Fact]
    public void A_body_that_is_not_json_is_shown_cut_to_its_limit()
    {
        var body = "not json " + new string('x', WhatsAppRawPayloadView.MaxShownTextLength);

        var view = WhatsAppRawPayloadView.Render(Encoding.UTF8.GetBytes(body));

        Assert.StartsWith("not json ", view, StringComparison.Ordinal);
        Assert.EndsWith("[9 more characters not shown]", view, StringComparison.Ordinal);
    }

    private static (string Body, string Base64) ImageMessage(string messageId, int rawImageBytes, string caption)
    {
        var image = new byte[rawImageBytes];
        new Random(765).NextBytes(image);
        var base64 = Convert.ToBase64String(image);
        var encodedCaption = JsonSerializer.Serialize(caption, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

        // The last two braces are added on their own: a $$ raw string cannot end in three.
        var body = $$"""
            {"event":"message.received","timestamp":"2026-10-16T09:00:00.000Z","sessionId":"{{SessionId}}","idempotencyKey":"msg_unknown","deliveryId":"dlv_{{messageId}}","data":{"id":"{{messageId}}","from":"263771234567@c.us","to":"263717068336@c.us","chatId":"263771234567@c.us","body":{{encodedCaption}},"type":"image","timestamp":1760605200,"fromMe":false,"isGroup":false,"media":{"mimetype":"image/jpeg","data":"{{base64}}"}
            """ + "}}";
        return (body, base64);
    }

    private async Task<int> StoredIdAsync()
    {
        using var context = _harness.NewContext();
        return await context.WhatsAppWebhookEvents.Select(message => message.Id).SingleAsync();
    }

    private static HttpClient Client(WebApplication app, bool signedIn, string role = "Admin")
    {
        var http = new HttpClient
        {
            BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()),
            Timeout = TimeSpan.FromSeconds(60)
        };

        if (signedIn)
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", role);
        }

        return http;
    }

    private async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        builder.Services.AddSingleton(ForwardingMediator());
        builder.Services.AddSingleton(StubProxy.Unused<IOpenWAClient>());
        builder.Services.AddSingleton(StubProxy.Unused<IOpenWAWebhookRegistrar>());
        builder.Services.AddProblemDetails();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, SignedInAsTheRoleAsked>("Test", _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy("AdminOnly", policy => policy.RequireRole("Admin")));
        builder.Services.AddControllers().ConfigureApplicationPartManager(parts =>
        {
            parts.ApplicationParts.Add(new AssemblyPart(typeof(WhatsAppController).Assembly));
            parts.FeatureProviders.Add(new OnlyController<WhatsAppController>());
        });

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();
        return app;
    }

    /// <summary>Hands the inbox queries to the real handlers, each on a context of its own.</summary>
    private IMediator ForwardingMediator() => StubProxy.For<IMediator>((method, args) => args?[0] switch
    {
        GetWhatsAppInboxQuery query => SendAsync(context => new GetWhatsAppInboxHandler(context).Handle(query, CancellationToken.None)),
        GetWhatsAppRawPayloadQuery query => SendAsync(context => new GetWhatsAppRawPayloadHandler(context).Handle(query, CancellationToken.None)),
        _ => throw new InvalidOperationException($"IMediator.{method.Name} was not expected to be called.")
    });

    private async Task<T> SendAsync<T>(Func<ShopInventory.Data.ApplicationDbContext, Task<T>> handle)
    {
        using var context = _harness.NewContext();
        return await handle(context);
    }

    /// <summary>Signs the request in with the role named in its Authorization header.</summary>
    private sealed class SignedInAsTheRoleAsked(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!AuthenticationHeaderValue.TryParse(Request.Headers.Authorization, out var header) || header.Parameter is null)
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "operator"), new Claim(ClaimTypes.Role, header.Parameter)], "Test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }

    private sealed class OnlyController<TController> : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            foreach (var controller in feature.Controllers.Where(type => type.AsType() != typeof(TController)).ToList())
                feature.Controllers.Remove(controller);
        }
    }
}
