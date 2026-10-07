using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Idempotency;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.WhatsApp.Commands.ReceiveOpenWAWebhook;

namespace ShopInventory.Tests;

/// <summary>
/// A contact's WhatsApp Status post is acknowledged but never stored.
/// </summary>
/// <remarks>
/// On 2026-10-07 a used-car advert appeared in the inbox as a "+status@broadcast" thread on the
/// fiscal-alerts session. Nobody had written to us: a contact had posted it as their Status, and
/// WhatsApp hands Status posts to the session as ordinary messages on <c>status@broadcast</c>.
/// </remarks>
public sealed class WhatsAppStatusPostTests : IDisposable
{
    private const string Secret = "status-post-test-secret";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public WhatsAppStatusPostTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new ApplicationDbContext(options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Theory]
    [InlineData("""{"event":"message.received","sessionId":"fiscal-alerts","data":{"id":"false_status@broadcast_3A1","from":"status@broadcast","author":"263771234567@c.us","body":"*2018 MAZDA AXELA SPORT*","fromMe":false}}""")]
    [InlineData("""{"event":"message.received","sessionId":"fiscal-alerts","data":{"id":{"remote":"status@broadcast","fromMe":false},"author":"263771234567@c.us","body":"advert"}}""")]
    [InlineData("""{"event":"message.received","sessionId":"fiscal-alerts","data":{"chatId":"status@broadcast","from":"263771234567@c.us","body":"advert"}}""")]
    [InlineData("""{"event":"message.sent","sessionId":"fiscal-alerts","data":{"from":"263717068336@c.us","to":"status@broadcast","body":"our own status","fromMe":true}}""")]
    [InlineData("""{"event":"message.received","sessionId":"fiscal-alerts","data":{"from":"263771234567@c.us","isStatus":true,"body":"advert"}}""")]
    public async Task Status_post_is_acknowledged_and_not_stored(string payload)
    {
        var result = await Receive(payload);

        Assert.False(result.IsError);
        Assert.Equal(0, result.Value.Id);
        Assert.Equal(0, await _context.WhatsAppWebhookEvents.CountAsync());
    }

    [Fact]
    public async Task Direct_message_is_still_stored()
    {
        var result = await Receive(
            """{"event":"message.received","sessionId":"fiscal-alerts","data":{"id":"false_263771234567@c.us_3A2","from":"263771234567@c.us","body":"Delivery path check","fromMe":false}}""");

        Assert.False(result.IsError);
        var stored = Assert.Single(await _context.WhatsAppWebhookEvents.ToListAsync());
        Assert.Equal(result.Value.Id, stored.Id);
        Assert.Equal("263771234567@c.us", stored.ChatId);
        Assert.Equal("Delivery path check", stored.TextBody);
    }

    private Task<ErrorOr.ErrorOr<ShopInventory.DTOs.WhatsAppWebhookReceiptDto>> Receive(string payload)
    {
        var handler = new ReceiveOpenWAWebhookHandler(
            _context,
            Options.Create(new OpenWASettings { Enabled = true, WebhookSecret = Secret }),
            StubProxy.Unused<IIdempotencyRequestStore>(),
            NullLogger<ReceiveOpenWAWebhookHandler>.Instance);

        return handler.Handle(
            new ReceiveOpenWAWebhookCommand(payload, Sign(payload), null, null, null, "/api/whatsapp/webhook"),
            CancellationToken.None);
    }

    private static string Sign(string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Secret));
        return $"sha256={Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant()}";
    }
}
