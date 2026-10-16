using System.Net;
using System.Text;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShopInventory.Controllers;
using ShopInventory.Features.WhatsApp.Commands.ReceiveOpenWAWebhook;
using ShopInventory.Middleware;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// A received attachment reaches the inbox whole. Proved by posting signed bodies through the real
/// size-limit and request-validation middleware to the real <see cref="WhatsAppController"/> and
/// handler, in the order Program.cs runs them.
/// </summary>
/// <remarks>
/// OpenWA puts a received attachment in the body as base64 (<c>data.media.data</c>). The route had
/// no <c>[MaxRequestBodySize]</c>, so the middleware's 1 MB default answered anything over about
/// 750 KB 413; OpenWA retried three times and dropped it. Reproduced on 2026-10-16 with a 2.4 MB
/// image body against an API built from #762.
/// </remarks>
public sealed class WhatsAppWebhookSizeLimitTests : IDisposable
{
    private const string SessionId = "3270941b-0000-4000-8000-000000000001";
    private const int RouteLimit = 20 * 1024 * 1024;

    private readonly WhatsAppWebhookHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task An_image_message_over_one_megabyte_is_accepted_and_stored_whole()
    {
        await using var app = await StartAsync();
        using var http = Client(app);
        var body = ImageMessage("false_263771234567@c.us_3EB0F1", rawImageBytes: 1_800_000, caption: "Proof of payment");
        Assert.InRange(Encoding.UTF8.GetByteCount(body), 2 * 1024 * 1024, RouteLimit);

        using var response = await PostAsync(http, body);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var context = _harness.NewContext();
        var stored = await context.WhatsAppWebhookEvents.SingleAsync();
        Assert.Equal("Proof of payment", stored.TextBody);
        Assert.Equal("image", stored.MessageType);
        Assert.Equal("263771234567@c.us", stored.SenderNumber);
        Assert.Equal("inbound", stored.Direction);
        Assert.Equal(body, stored.RawPayload);
    }

    [Fact]
    public async Task A_redelivery_of_a_large_message_replays_its_receipt()
    {
        await using var app = await StartAsync();
        using var http = Client(app);
        var body = ImageMessage("false_263771234567@c.us_3EB0F2", rawImageBytes: 1_800_000, caption: "Again");

        using var first = await PostAsync(http, body);
        using var again = await PostAsync(http, body);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await again.Content.ReadAsStringAsync());
        using var context = _harness.NewContext();
        Assert.Equal(1, await context.WhatsAppWebhookEvents.CountAsync());
    }

    [Fact]
    public async Task A_body_over_the_routes_limit_is_still_refused()
    {
        await using var app = await StartAsync();
        using var http = Client(app);
        var body = ImageMessage("false_263771234567@c.us_3EB0F3", rawImageBytes: 16 * 1024 * 1024, caption: "Video");
        Assert.True(Encoding.UTF8.GetByteCount(body) > RouteLimit);

        using var response = await PostAsync(http, body);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        using var context = _harness.NewContext();
        Assert.Equal(0, await context.WhatsAppWebhookEvents.CountAsync());
    }

    [Fact]
    public async Task Another_json_route_keeps_the_one_megabyte_default()
    {
        await using var app = await StartAsync();
        using var http = Client(app);
        var body = ImageMessage("false_263771234567@c.us_3EB0F4", rawImageBytes: 1_800_000, caption: "Wrong door");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/whatsapp/sessions/{SessionId}/messages/send-text")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.ExpectContinue = true;
        using var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    /// <summary>
    /// The body OpenWA posts for a received image, its file downloaded into <c>data.media</c>.
    /// The bytes are seeded, so the request validation scan sees the same body on every run.
    /// </summary>
    private static string ImageMessage(string messageId, int rawImageBytes, string caption)
    {
        var image = new byte[rawImageBytes];
        new Random(764).NextBytes(image);

        // The last two braces are added on their own: a $$ raw string cannot end in three.
        return $$"""
            {"event":"message.received","timestamp":"2026-10-16T09:00:00.000Z","sessionId":"{{SessionId}}","idempotencyKey":"msg_unknown","deliveryId":"dlv_{{messageId}}","data":{"id":"{{messageId}}","from":"263771234567@c.us","to":"263717068336@c.us","chatId":"263771234567@c.us","body":"{{caption}}","type":"image","timestamp":1760605200,"fromMe":false,"isGroup":false,"media":{"mimetype":"image/jpeg","data":"{{Convert.ToBase64String(image)}}"}
            """ + "}}";
    }

    /// <summary>
    /// Posts as OpenWA does. <c>Expect: 100-continue</c> lets a refusal by Content-Length arrive
    /// before the client has sent a 20 MB body the server will not read.
    /// </summary>
    private static Task<HttpResponseMessage> PostAsync(HttpClient http, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/whatsapp/webhook/openwa")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-OpenWA-Signature", WhatsAppWebhookHarness.Sign(body));
        request.Headers.Add("X-OpenWA-Event", "message.received");
        request.Headers.ExpectContinue = true;
        return http.SendAsync(request);
    }

    private static HttpClient Client(WebApplication app) => new()
    {
        BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()),
        Timeout = TimeSpan.FromSeconds(60)
    };

    private async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        builder.Services.AddSingleton(ForwardingMediator());
        builder.Services.AddSingleton(StubProxy.Unused<IOpenWAClient>());
        builder.Services.AddSingleton(StubProxy.Unused<IOpenWAWebhookRegistrar>());
        builder.Services.AddProblemDetails();
        builder.Services.AddAuthorization(options => options.AddPolicy("AdminOnly", policy => policy.RequireRole("Admin")));
        builder.Services.AddControllers().ConfigureApplicationPartManager(parts =>
        {
            parts.ApplicationParts.Add(new AssemblyPart(typeof(WhatsAppController).Assembly));
            parts.FeatureProviders.Add(new OnlyController<WhatsAppController>());
        });

        // No explicit UseRouting, as in Program.cs: WebApplication routes first, so the size limit
        // middleware sees the endpoint and its [MaxRequestBodySize].
        var app = builder.Build();
        app.UseRequestSizeLimit();
        app.UseRequestValidation();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();
        return app;
    }

    /// <summary>Hands the webhook command to the real handler over the harness's database.</summary>
    private IMediator ForwardingMediator() => StubProxy.For<IMediator>((method, args) =>
        args?[0] is ReceiveOpenWAWebhookCommand command
            ? _harness.Handle(command)
            : throw new InvalidOperationException($"IMediator.{method.Name} was not expected to be called."));

    private sealed class OnlyController<TController> : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            foreach (var controller in feature.Controllers.Where(type => type.AsType() != typeof(TController)).ToList())
                feature.Controllers.Remove(controller);
        }
    }
}
