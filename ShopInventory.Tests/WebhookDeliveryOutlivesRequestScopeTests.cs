using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// A webhook delivery must still write its log row after the request that raised the event is gone.
/// </summary>
/// <remarks>
/// <c>TriggerEventAsync</c> hands delivery to <c>Task.Run</c> and returns, so the work outlives the
/// request by design — the retry loop alone can run to 2+4+8 seconds. It used to do that work on the
/// request's own scoped <see cref="ApplicationDbContext"/>, so every delivery-row insert and every
/// counter save threw <see cref="ObjectDisposedException"/> into a catch that logged and moved on.
///
/// The POST still went out, which is why nothing looked wrong: what was lost was the delivery log,
/// the only thing the admin page can diagnose a failed hook from, and the success/failure counters
/// on the hook itself. An operator chasing a missed event would have seen an empty log — exactly
/// what a hook nobody triggered looks like.
///
/// Asserting on the POST would therefore pass against the broken version. The assertion has to be
/// the row. A shared in-memory SQLite connection stands in for the database precisely because a
/// second context opened on it — the delivery's own scope — sees what the first one wrote.
/// </remarks>
public sealed class WebhookDeliveryOutlivesRequestScopeTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    public WebhookDeliveryOutlivesRequestScopeTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        using (var seed = NewContext())
        {
            seed.Database.EnsureCreated();
        }

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(
            options => options.UseSqlite(_connection),
            ServiceLifetime.Scoped);
        services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(HttpStatusCode.OK));
        services.AddSingleton<ILogger<WebhookService>>(NullLogger<WebhookService>.Instance);
        services.AddScoped<IWebhookService, WebhookService>();

        _provider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task Delivery_writes_its_log_row_after_the_requests_scope_is_disposed()
    {
        int webhookId;
        using (var arrange = NewContext())
        {
            var webhook = new Webhook
            {
                Name = "Scope test",
                Url = "https://receiver.invalid/hook",
                Events = WebhookEventTypes.InvoiceCreated,
                IsActive = true,
                RetryCount = 3,
                TimeoutSeconds = 5
            };
            arrange.Webhooks.Add(webhook);
            await arrange.SaveChangesAsync();
            webhookId = webhook.Id;
        }

        // The request: raise the event, then end the request before delivery can finish.
        using (var requestScope = _provider.CreateScope())
        {
            var service = requestScope.ServiceProvider.GetRequiredService<IWebhookService>();
            await service.TriggerEventAsync(WebhookEventTypes.InvoiceCreated, new { invoiceId = 4711 });
        }

        var delivery = await WaitForDeliveryAsync();

        Assert.NotNull(delivery);
        Assert.Equal(WebhookEventTypes.InvoiceCreated, delivery!.EventType);
        Assert.True(delivery.IsSuccess);
        Assert.Equal(200, delivery.ResponseStatusCode);

        // The counters live on the hook and were saved from that same disposed-scope context.
        using var assertContext = NewContext();
        var saved = await assertContext.Webhooks.SingleAsync(w => w.Id == webhookId);
        Assert.Equal(1, saved.SuccessCount);
        Assert.NotNull(saved.LastTriggeredAt);
    }

    private Task<WebhookDelivery?> WaitForDeliveryAsync()
        => BackgroundWriteProbe.PollAsync<WebhookDelivery>(
            NewContext,
            async context =>
            {
                var delivery = await context.WebhookDeliveries.FirstOrDefaultAsync();

                // The counter save is the delivery's last write, so waiting for it keeps the
                // caller's own reads off a connection the background task is still using.
                var settled = await context.Webhooks.AnyAsync(w => w.LastTriggeredAt != null);

                return delivery != null && settled ? delivery : null;
            });

    private ApplicationDbContext NewContext()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options);

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    private sealed class StubHttpClientFactory(HttpStatusCode status) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler(status));

        private sealed class StubHandler(HttpStatusCode status) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(status)
                {
                    Content = new StringContent("ok")
                });
        }
    }
}
