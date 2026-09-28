using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using ShopInventory.Web.Data;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The nightly retention rules: for each table, the rows just past its cut-off go, the rows just
/// inside it stay, and the rows it must never touch (a blocked client, an email still being
/// retried, a live idempotency lease, a device still in use) stay whatever their age.
/// </summary>
/// <remarks>
/// Foreign keys are off: every table here is a leaf, which is what makes deleting from it safe,
/// and the parents (users, webhooks, customer accounts) play no part in any rule.
/// </remarks>
public sealed class DatabaseRetentionTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 28, 2, 15, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _apiConnection = Open();
    private readonly SqliteConnection _webConnection = Open();
    private readonly List<(object Entity, bool Doomed)> _seeded = [];

    public void Dispose()
    {
        _apiConnection.Dispose();
        _webConnection.Dispose();
    }

    [Fact]
    public async Task Api_rules_delete_exactly_the_rows_past_their_cut_off()
    {
        await using (var db = ApiContext())
        {
            db.Database.EnsureCreated();

            Seed(db, Notification(expiresAt: Now.AddDays(-91)), doomed: true);
            Seed(db, Notification(expiresAt: Now.AddDays(-89)), doomed: false);
            Seed(db, Notification(expiresAt: null), doomed: false);

            Seed(db, new WebhookDelivery { WebhookId = 1, CreatedAt = Now.AddDays(-91) }, doomed: true);
            Seed(db, new WebhookDelivery { WebhookId = 1, CreatedAt = Now.AddDays(-89) }, doomed: false);

            Seed(db, Email("Sent", attempts: 1, createdAt: Now.AddDays(-31)), doomed: true);
            Seed(db, Email("Sent", attempts: 1, createdAt: Now.AddDays(-29)), doomed: false);
            Seed(db, Email("Failed", attempts: 3, createdAt: Now.AddDays(-91)), doomed: true);
            Seed(db, Email("Failed", attempts: 3, createdAt: Now.AddDays(-89)), doomed: false);
            Seed(db, Email("Failed", attempts: 2, createdAt: Now.AddDays(-400)), doomed: false);
            Seed(db, Email("Pending", attempts: 0, createdAt: Now.AddDays(-400)), doomed: false);

            Seed(db, StaffToken(expiresAt: Now.AddDays(-31)), doomed: true);
            Seed(db, StaffToken(expiresAt: Now.AddDays(-29)), doomed: false);

            Seed(db, CustomerAppToken(expiresAt: Now.AddDays(-91)), doomed: true);
            Seed(db, CustomerAppToken(expiresAt: Now.AddDays(-89)), doomed: false);

            Seed(db, Claim(IdempotencyRequestStatus.Completed, expiresAt: Now.AddDays(-2)), doomed: true);
            Seed(db, Claim(IdempotencyRequestStatus.Completed, expiresAt: Now.AddHours(-12)), doomed: false);
            Seed(db, Claim(IdempotencyRequestStatus.InProgress, expiresAt: Now.AddMinutes(5)), doomed: false);

            Seed(db, OfflineItem("Completed", completedAt: Now.AddDays(-31)), doomed: true);
            Seed(db, OfflineItem("Completed", completedAt: Now.AddDays(-29)), doomed: false);
            Seed(db, OfflineItem("Failed", completedAt: null), doomed: false);

            Seed(db, RateLimit(blocked: false, lastRequestAt: Now.AddDays(-31)), doomed: true);
            Seed(db, RateLimit(blocked: false, lastRequestAt: Now.AddDays(-29)), doomed: false);
            Seed(db, RateLimit(blocked: true, lastRequestAt: Now.AddDays(-400)), doomed: false);

            Seed(db, StaffDevice(revoked: true, lastActiveAt: Now.AddDays(-91)), doomed: true);
            Seed(db, StaffDevice(revoked: true, lastActiveAt: Now.AddDays(-89)), doomed: false);
            Seed(db, StaffDevice(revoked: false, lastActiveAt: Now.AddDays(-400)), doomed: false);
            Seed(db, StaffDevice(revoked: true, lastActiveAt: null, registeredAt: Now.AddDays(-91)), doomed: true);

            Seed(db, CustomerAppDevice(revoked: true, lastActiveAt: Now.AddDays(-91)), doomed: true);
            Seed(db, CustomerAppDevice(revoked: true, lastActiveAt: Now.AddDays(-89)), doomed: false);
            Seed(db, CustomerAppDevice(revoked: false, lastActiveAt: Now.AddDays(-400)), doomed: false);

            Seed(db, PasswordReset(expiresAt: Now.AddDays(-31)), doomed: true);
            Seed(db, PasswordReset(expiresAt: Now.AddDays(-29)), doomed: false);

            Seed(db, Otp(expiresAt: Now.AddDays(-8)), doomed: true);
            Seed(db, Otp(expiresAt: Now.AddDays(-6)), doomed: false);
        }

        IReadOnlyList<(string Table, int Count)> removed;
        await using (var db = ApiContext())
        {
            removed = await DatabaseRetentionJob.PurgeAsync(db, Now, batchSize: 5000, CancellationToken.None);
        }

        await using (var db = ApiContext())
        {
            await AssertOnlyDoomedRowsAreGoneAsync(db);
        }

        // Every rule ran, and the counts it reports are what it deleted.
        Assert.Equal(DatabaseRetentionJob.Rules.Select(rule => rule.Table), removed.Select(entry => entry.Table));
        Assert.Equal(_seeded.Count(entry => entry.Doomed), removed.Sum(entry => entry.Count));
    }

    [Fact]
    public async Task Api_rules_delete_a_backlog_larger_than_one_batch()
    {
        await using (var db = ApiContext())
        {
            db.Database.EnsureCreated();
            for (var i = 0; i < 7; i++)
            {
                Seed(db, Notification(expiresAt: Now.AddDays(-100 - i)), doomed: true);
            }

            Seed(db, Notification(expiresAt: Now.AddDays(-10)), doomed: false);
        }

        await using (var db = ApiContext())
        {
            var removed = await DatabaseRetentionJob.PurgeAsync(db, Now, batchSize: 3, CancellationToken.None);
            Assert.Equal(7, removed.Single(entry => entry.Table == "Notifications").Count);
        }

        await using (var db = ApiContext())
        {
            await AssertOnlyDoomedRowsAreGoneAsync(db);
        }
    }

    [Fact]
    public async Task Customer_portal_rules_delete_exactly_the_rows_past_their_cut_off()
    {
        await using (var db = WebContext())
        {
            db.Database.EnsureCreated();

            Seed(db, SecurityLog(Now.AddDays(-366)), doomed: true);
            Seed(db, SecurityLog(Now.AddDays(-364)), doomed: false);

            Seed(db, PortalToken(expiresAt: Now.AddDays(-91)), doomed: true);
            Seed(db, PortalToken(expiresAt: Now.AddDays(-89)), doomed: false);

            Seed(db, PortalRateLimit(windowStart: Now.AddDays(-8), blockedUntil: null), doomed: true);
            Seed(db, PortalRateLimit(windowStart: Now.AddDays(-8), blockedUntil: Now.AddDays(-1)), doomed: true);
            Seed(db, PortalRateLimit(windowStart: Now.AddDays(-8), blockedUntil: Now.AddHours(1)), doomed: false);
            Seed(db, PortalRateLimit(windowStart: Now.AddDays(-6), blockedUntil: null), doomed: false);
        }

        IReadOnlyList<(string Table, int Count)> removed;
        await using (var db = WebContext())
        {
            removed = await CustomerPortalRetentionService.PurgeAsync(db, Now, batchSize: 2, CancellationToken.None);
        }

        await using (var db = WebContext())
        {
            await AssertOnlyDoomedRowsAreGoneAsync(db);
        }

        Assert.Equal([("CustomerSecurityLogs", 1), ("CustomerRefreshTokens", 1), ("CustomerRateLimits", 2)], removed);
    }

    private void Seed(DbContext db, object entity, bool doomed)
    {
        db.Add(entity);
        db.SaveChanges();
        _seeded.Add((entity, doomed));
    }

    /// <summary>Looks every seeded row up again by its key: the doomed ones must be gone, the rest there.</summary>
    private async Task AssertOnlyDoomedRowsAreGoneAsync(DbContext db)
    {
        foreach (var (entity, doomed) in _seeded.Where(entry => db.Model.FindEntityType(entry.Entity.GetType()) is not null))
        {
            var entityType = db.Model.FindEntityType(entity.GetType())!;
            var key = entityType.FindPrimaryKey()!.Properties.Single().PropertyInfo!.GetValue(entity);
            var found = await db.FindAsync(entity.GetType(), key);

            Assert.True(
                doomed ? found is null : found is not null,
                $"{entityType.GetTableName()} row {key} should have been {(doomed ? "deleted" : "kept")}.");
        }
    }

    private ApplicationDbContext ApiContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_apiConnection).Options);

    private WebAppDbContext WebContext() =>
        new(new DbContextOptionsBuilder<WebAppDbContext>().UseSqlite(_webConnection).Options);

    private static SqliteConnection Open()
    {
        var connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=False");
        connection.Open();
        return connection;
    }

    private static Notification Notification(DateTime? expiresAt) => new()
    {
        Title = "Title",
        Message = "Message",
        Type = "Info",
        Category = "System",
        CreatedAt = (expiresAt ?? Now).AddDays(-30),
        ExpiresAt = expiresAt
    };

    private static EmailQueueItem Email(string status, int attempts, DateTime createdAt) => new()
    {
        ToAddresses = "someone@example.com",
        Subject = "Subject",
        Body = "Body",
        Status = status,
        AttemptCount = attempts,
        CreatedAt = createdAt
    };

    private static RefreshToken StaffToken(DateTime expiresAt) => new()
    {
        Id = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        TokenHash = Guid.NewGuid().ToString("N"),
        CreatedAt = expiresAt.AddDays(-7),
        ExpiresAt = expiresAt
    };

    private static VanSalesCustomerRefreshTokenEntity CustomerAppToken(DateTime expiresAt) => new()
    {
        Id = Guid.NewGuid(),
        VanSalesCustomerAccountId = 1,
        TokenHash = Guid.NewGuid().ToString("N"),
        DeviceId = "device",
        CreatedAt = expiresAt.AddDays(-90),
        ExpiresAt = expiresAt
    };

    private static IdempotencyRequestEntity Claim(IdempotencyRequestStatus status, DateTime expiresAt) => new()
    {
        Scope = "test",
        IdempotencyKey = Guid.NewGuid().ToString("N"),
        RequestHash = "hash",
        Status = status,
        CreatedAtUtc = expiresAt.AddHours(-1),
        ExpiresAtUtc = expiresAt
    };

    private static OfflineQueueItem OfflineItem(string status, DateTime? completedAt) => new()
    {
        TransactionType = "Invoice",
        TransactionData = "{}",
        Status = status,
        CreatedAt = (completedAt ?? Now).AddDays(-1),
        CompletedAt = completedAt
    };

    private static ApiRateLimitEntity RateLimit(bool blocked, DateTime lastRequestAt) => new()
    {
        ClientId = Guid.NewGuid().ToString("N"),
        IsBlocked = blocked,
        WindowStart = lastRequestAt,
        LastRequestAt = lastRequestAt
    };

    private static PushDeviceRegistration StaffDevice(bool revoked, DateTime? lastActiveAt, DateTime? registeredAt = null) => new()
    {
        UserId = Guid.NewGuid(),
        DeviceToken = Guid.NewGuid().ToString("N"),
        Platform = "android",
        IsRevoked = revoked,
        RegisteredAt = registeredAt ?? Now.AddDays(-500),
        LastActiveAt = lastActiveAt
    };

    private static VanSalesCustomerDeviceEntity CustomerAppDevice(bool revoked, DateTime? lastActiveAt) => new()
    {
        VanSalesCustomerAccountId = 1,
        DeviceToken = Guid.NewGuid().ToString("N"),
        IsRevoked = revoked,
        RegisteredAt = Now.AddDays(-500),
        LastActiveAt = lastActiveAt
    };

    private static PasswordResetToken PasswordReset(DateTime expiresAt) => new()
    {
        Id = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        TokenHash = Guid.NewGuid().ToString("N"),
        CreatedAt = expiresAt.AddHours(-1),
        ExpiresAt = expiresAt
    };

    private static VanSalesCustomerOtpEntity Otp(DateTime expiresAt) => new()
    {
        PhoneE164 = "+263770000000",
        CodeHash = "hash",
        CreatedAt = expiresAt.AddMinutes(-10),
        ExpiresAt = expiresAt
    };

    private static CustomerSecurityLog SecurityLog(DateTime timestamp) => new()
    {
        CardCode = "C001",
        Action = "Login",
        Timestamp = timestamp
    };

    private static CustomerRefreshToken PortalToken(DateTime expiresAt) => new()
    {
        CardCode = "C001",
        TokenHash = Guid.NewGuid().ToString("N"),
        ExpiresAt = expiresAt
    };

    private static CustomerRateLimit PortalRateLimit(DateTime windowStart, DateTime? blockedUntil) => new()
    {
        Identifier = Guid.NewGuid().ToString("N"),
        Endpoint = "login",
        WindowStart = windowStart,
        WindowEnd = windowStart.AddMinutes(1),
        IsBlocked = blockedUntil is not null,
        BlockedUntil = blockedUntil
    };
}
