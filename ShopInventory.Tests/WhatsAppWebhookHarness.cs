using System.Security.Cryptography;
using System.Text;
using ErrorOr;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Idempotency;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.WhatsApp.Commands.ReceiveOpenWAWebhook;

namespace ShopInventory.Tests;

/// <summary>
/// Drives <see cref="ReceiveOpenWAWebhookHandler"/> over one in-memory SQLite database, with the
/// real idempotency store on its own context per scope as it runs in production.
/// </summary>
internal sealed class WhatsAppWebhookHarness : IDisposable
{
    private const string Secret = "openwa-webhook-test-secret";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ApplicationDbContext> _options;

    public WhatsAppWebhookHarness()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = new ApplicationDbContext(_options);
        context.Database.EnsureCreated();
    }

    public ApplicationDbContext NewContext() => new(_options);

    public async Task<ErrorOr<WhatsAppWebhookReceiptDto>> Receive(
        string payload,
        string? idempotencyKey = null,
        string? deliveryId = null)
    {
        using var context = NewContext();
        var handler = new ReceiveOpenWAWebhookHandler(
            context,
            Options.Create(new OpenWASettings { Enabled = true, WebhookSecret = Secret }),
            new IdempotencyRequestStore(new SingleContextScopeFactory(_options), Options.Create(new SecuritySettings())),
            NullLogger<ReceiveOpenWAWebhookHandler>.Instance);

        return await handler.Handle(
            new ReceiveOpenWAWebhookCommand(payload, Sign(payload), null, idempotencyKey, deliveryId, "/api/whatsapp/webhook/openwa"),
            CancellationToken.None);
    }

    public void Dispose() => _connection.Dispose();

    private static string Sign(string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Secret));
        return $"sha256={Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant()}";
    }

    private sealed class SingleContextScopeFactory(DbContextOptions<ApplicationDbContext> options)
        : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        public IServiceScope CreateScope() => this;

        public IServiceProvider ServiceProvider => this;

        public object? GetService(Type serviceType)
            => serviceType == typeof(ApplicationDbContext) ? new ApplicationDbContext(options) : null;

        public void Dispose()
        {
        }
    }
}
