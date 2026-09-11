using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.Features.VanSalesCustomerAuth;
using ShopInventory.Features.VanSalesCustomerAuth.Commands.LogoutVanSalesCustomer;
using ShopInventory.Features.VanSalesCustomerAuth.Commands.RefreshVanSalesCustomerSession;
using ShopInventory.Features.VanSalesOrders.Commands.RegisterVanSalesCustomerDevice;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// What the customer app's session endpoints leave behind.
///
/// The refresh replay is the reason this file exists. A refresh token presented after it has been
/// rotated is either the app retrying or somebody replaying a stolen copy, and the handler cannot
/// tell which — so it cuts the whole device's chain and makes both parties sign in again. That is
/// the strongest signal of account compromise this system produces, and until now it existed only
/// as a line in the application log, which nobody reads and nothing retains.
/// </summary>
public sealed class VanSalesCustomerSessionAuditTests : IDisposable
{
    private const int AccountId = 1;
    private const string DeviceId = "handset-7";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly RecordingAuditService _audit = new();

    public VanSalesCustomerSessionAuditTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new SqliteApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task A_replayed_refresh_token_is_recorded_as_the_theft_signal_it_is()
    {
        var token = await GivenRefreshTokenAsync(revoked: true);

        var result = await RefreshAsync(token);

        Assert.True(result.IsError);

        var row = Assert.Single(_audit.Entries);
        Assert.Equal(AuditActions.VanSalesCustomerSessionRefresh, row.Action);
        Assert.False(row.Success);
        Assert.Equal(AccountId.ToString(), row.EntityId);
        Assert.Contains("already rotated or revoked", row.Details);
        Assert.Contains(DeviceId, row.Details);
        Assert.Equal("Refresh token replay", row.Error);
    }

    /// <summary>
    /// A token this server never issued names no account, so the row cannot either. It is still
    /// written: a run of them against one endpoint is worth seeing even when none of them lands.
    /// </summary>
    [Fact]
    public async Task An_unknown_refresh_token_still_leaves_a_row()
    {
        var result = await RefreshAsync("nothing-this-server-issued");

        Assert.True(result.IsError);

        var row = Assert.Single(_audit.Entries);
        Assert.Equal(AuditActions.VanSalesCustomerSessionRefresh, row.Action);
        Assert.False(row.Success);
        Assert.Null(row.EntityId);
    }

    [Fact]
    public async Task An_expired_refresh_token_is_told_apart_from_a_replay()
    {
        var token = await GivenRefreshTokenAsync(revoked: false, expiresAt: DateTime.UtcNow.AddDays(-1));

        await RefreshAsync(token);

        var row = Assert.Single(_audit.Entries);
        Assert.Contains("expired", row.Details);
        Assert.NotEqual("Refresh token replay", row.Error);
    }

    [Fact]
    public async Task Signing_out_records_how_many_sessions_it_ended()
    {
        await GivenRefreshTokenAsync(revoked: false);

        var handler = new LogoutVanSalesCustomerHandler(
            _context, _audit, NullLogger<LogoutVanSalesCustomerHandler>.Instance);

        var result = await handler.Handle(
            new LogoutVanSalesCustomerCommand(AccountId, null, DeviceId), CancellationToken.None);

        Assert.False(result.IsError);

        var row = Assert.Single(_audit.Entries);
        Assert.Equal(AuditActions.VanSalesCustomerSignOut, row.Action);
        Assert.True(row.Success);
        Assert.Contains("1 token(s) revoked", row.Details);
    }

    /// <summary>
    /// A push token turning up against a different account means the handset changed hands — a
    /// shared phone, or a shop sold. Afterwards the device row shows only where it ended up.
    /// </summary>
    [Fact]
    public async Task A_push_device_taken_over_from_another_account_says_so()
    {
        await GivenAccountAsync(AccountId);
        await GivenAccountAsync(2);

        _context.VanSalesCustomerDevices.Add(new VanSalesCustomerDeviceEntity
        {
            VanSalesCustomerAccountId = 2,
            DeviceToken = "fcm-token",
            RegisteredAt = DateTime.UtcNow,
            LastActiveAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        var handler = new RegisterVanSalesCustomerDeviceHandler(
            _context, _audit, NullLogger<RegisterVanSalesCustomerDeviceHandler>.Instance);

        var result = await handler.Handle(
            new RegisterVanSalesCustomerDeviceCommand(AccountId, "fcm-token", DeviceId, "Shop phone", "1.4.0"),
            CancellationToken.None);

        Assert.False(result.IsError);

        var row = Assert.Single(_audit.Entries);
        Assert.Equal(AuditActions.RegisterVanSalesCustomerDevice, row.Action);
        Assert.True(row.Success);
        Assert.Contains("taken over from account 2", row.Details);
    }

    [Fact]
    public async Task A_push_device_registered_for_the_first_time_claims_no_takeover()
    {
        await GivenAccountAsync(AccountId);

        var handler = new RegisterVanSalesCustomerDeviceHandler(
            _context, _audit, NullLogger<RegisterVanSalesCustomerDeviceHandler>.Instance);

        await handler.Handle(
            new RegisterVanSalesCustomerDeviceCommand(AccountId, "fcm-token", DeviceId, "Shop phone", "1.4.0"),
            CancellationToken.None);

        var row = Assert.Single(_audit.Entries);
        Assert.True(row.Success);
        Assert.DoesNotContain("taken over", row.Details);
    }

    // ---- Fixtures ---------------------------------------------------------------------------------

    private Task<ErrorOr.ErrorOr<VanSalesCustomerSessionResult>> RefreshAsync(string token)
    {
        // The issuer is never reached on any path under test here, and a stub that throws says so
        // rather than letting a wrong turn pass unnoticed.
        var handler = new RefreshVanSalesCustomerSessionHandler(
            _context,
            StubProxy.Unused<IVanSalesCustomerSessionIssuer>(),
            _audit,
            NullLogger<RefreshVanSalesCustomerSessionHandler>.Instance);

        return handler.Handle(
            new RefreshVanSalesCustomerSessionCommand(token, DeviceId, "Shop phone", "127.0.0.1"),
            CancellationToken.None);
    }

    private async Task<string> GivenRefreshTokenAsync(bool revoked, DateTime? expiresAt = null)
    {
        await GivenAccountAsync(AccountId);

        const string value = "the-refresh-token";

        _context.VanSalesCustomerRefreshTokens.Add(new VanSalesCustomerRefreshTokenEntity
        {
            VanSalesCustomerAccountId = AccountId,
            TokenHash = VanSalesCustomerRefreshTokenValue.Hash(value),
            DeviceId = DeviceId,
            DeviceName = "Shop phone",
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            ExpiresAt = expiresAt ?? DateTime.UtcNow.AddDays(30),
            RevokedAt = revoked ? DateTime.UtcNow.AddMinutes(-5) : null
        });

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        return value;
    }

    private async Task GivenAccountAsync(int accountId)
    {
        if (await _context.VanSalesCustomerAccounts.AnyAsync(a => a.Id == accountId))
        {
            return;
        }

        _context.RouteCustomers.Add(new RouteCustomerEntity
        {
            Id = accountId,
            AssignedBusinessPartnerCode = "BP-1",
            Code = $"CUST-{accountId}",
            Name = $"Shop {accountId}",
            IsActive = true
        });

        _context.VanSalesCustomerAccounts.Add(new VanSalesCustomerAccountEntity
        {
            Id = accountId,
            RouteCustomerId = accountId,
            PhoneE164 = $"+26377123456{accountId}",
            IsActive = true
        });

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    /// <summary>
    /// <see cref="DailyStockSnapshotItemEntity.Version"/> is <c>[Timestamp]</c>, mapped by Npgsql to
    /// the store-generated <c>xmin</c>. SQLite has no equivalent, so EnsureCreated's NOT NULL
    /// constraint fails on insert. Nothing here touches that entity.
    /// </summary>
    private sealed class SqliteApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : ApplicationDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<DailyStockSnapshotItemEntity>()
                .Property(item => item.Version)
                .ValueGeneratedNever()
                .IsConcurrencyToken(false);
        }
    }
}
