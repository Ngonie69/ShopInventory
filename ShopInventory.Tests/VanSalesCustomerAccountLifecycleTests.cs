using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.VanSalesCustomerAuth;
using ShopInventory.Features.VanSalesCustomerAuth.Commands.DeactivateVanSalesCustomerAccount;
using ShopInventory.Features.VanSalesCustomerAuth.Commands.OnboardVanSalesCustomerAccount;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Granting and withdrawing a shop's ability to order through the app.
///
/// Withdrawal is the half that has to be right. It gets used when a handset is lost, when a shop
/// changes hands, or when a customer and the business part company — every one of them a case where
/// "they stay signed in until the token runs out" is the wrong answer. Clearing the active flag
/// alone would do exactly that: the access token is already minted, and the refresh token would
/// keep renewing it for the ninety days it was issued for.
/// </summary>
public sealed class VanSalesCustomerAccountLifecycleTests : IDisposable
{
    private const string TypedNumber = "0771234567";
    private const string StoredNumber = "+263771234567";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public VanSalesCustomerAccountLifecycleTests()
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
    public async Task Onboarding_stores_the_number_in_its_canonical_form()
    {
        // The rep types it locally; sign-in looks it up in E.164. If onboarding stored what was
        // typed, the customer could never sign in and nothing would report an error.
        await GivenRouteCustomerAsync(1, "CUST-1");

        var result = await OnboardAsync(1, TypedNumber);

        Assert.False(result.IsError);
        Assert.Equal(StoredNumber, result.Value.PhoneE164);
    }

    [Fact]
    public async Task Onboarding_the_same_shop_again_updates_rather_than_duplicating()
    {
        // Reps re-run this on repeat visits. A second row would collide with the unique index.
        await GivenRouteCustomerAsync(1, "CUST-1");

        await OnboardAsync(1, TypedNumber, "Old Name");
        var second = await OnboardAsync(1, "+263 77 123 4567", "New Name");

        Assert.False(second.IsError);
        Assert.Equal("New Name", second.Value.DisplayName);
        Assert.Equal(1, await _context.VanSalesCustomerAccounts.CountAsync());
    }

    [Fact]
    public async Task A_number_already_signing_in_for_another_shop_is_refused()
    {
        // Honouring it would move the first shop's ordering onto a phone that is not theirs, and
        // their order history with it.
        await GivenRouteCustomerAsync(1, "CUST-1");
        await GivenRouteCustomerAsync(2, "CUST-2");

        await OnboardAsync(1, TypedNumber);
        var stolen = await OnboardAsync(2, TypedNumber);

        Assert.True(stolen.IsError);
        Assert.Equal("VanSalesCustomerAuth.PhoneAlreadyInUse", stolen.FirstError.Code);
    }

    [Fact]
    public async Task The_refusal_does_not_print_the_whole_number()
    {
        // The operator being told which number clashes does not need the other shop's full number.
        await GivenRouteCustomerAsync(1, "CUST-1");
        await GivenRouteCustomerAsync(2, "CUST-2");

        await OnboardAsync(1, TypedNumber);
        var stolen = await OnboardAsync(2, TypedNumber);

        Assert.DoesNotContain("263771", stolen.FirstError.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_inactive_shop_cannot_be_given_a_sign_in()
    {
        await GivenRouteCustomerAsync(1, "CUST-1", active: false);

        var result = await OnboardAsync(1, TypedNumber);

        Assert.True(result.IsError);
        Assert.Equal("VanSalesCustomerAuth.RouteCustomerInactive", result.FirstError.Code);
    }

    [Fact]
    public async Task Re_onboarding_lifts_a_lockout()
    {
        // The rep is in the shop confirming who this is, which is a better check than the one that
        // locked the account. Without this the customer waits out the lockout for no reason.
        await GivenRouteCustomerAsync(1, "CUST-1");
        await OnboardAsync(1, TypedNumber);

        await _context.VanSalesCustomerAccounts.ExecuteUpdateAsync(s => s
            .SetProperty(a => a.LockedUntil, DateTime.UtcNow.AddHours(1))
            .SetProperty(a => a.FailedOtpCount, 9));
        _context.ChangeTracker.Clear();

        var result = await OnboardAsync(1, TypedNumber);

        Assert.False(result.IsError);
        Assert.False(result.Value.IsLockedOut);
        Assert.Null((await _context.VanSalesCustomerAccounts.AsNoTracking().SingleAsync()).LockedUntil);
    }

    [Fact]
    public async Task Deactivating_revokes_every_live_session()
    {
        // The assertion this class exists for. A revoked account whose refresh tokens still work is
        // an account that is not revoked.
        await GivenRouteCustomerAsync(1, "CUST-1");
        var account = await OnboardAsync(1, TypedNumber);
        await GivenLiveSessionAsync(account.Value.Id, "device-a");
        await GivenLiveSessionAsync(account.Value.Id, "device-b");

        var result = await DeactivateAsync(account.Value.Id);

        Assert.False(result.IsError);
        Assert.False(result.Value.IsActive);

        var stillLive = await _context.VanSalesCustomerRefreshTokens
            .AsNoTracking()
            .CountAsync(t => t.RevokedAt == null);

        Assert.Equal(0, stillLive);
    }

    [Fact]
    public async Task Deactivating_keeps_the_row_so_past_orders_stay_attributable()
    {
        await GivenRouteCustomerAsync(1, "CUST-1");
        var account = await OnboardAsync(1, TypedNumber);

        await DeactivateAsync(account.Value.Id);

        Assert.Equal(1, await _context.VanSalesCustomerAccounts.CountAsync());
    }

    [Fact]
    public async Task Deactivating_something_that_is_not_there_reports_not_found()
    {
        var result = await DeactivateAsync(404);

        Assert.True(result.IsError);
        Assert.Equal("VanSalesCustomerAuth.AccountNotFound", result.FirstError.Code);
    }

    // ── Fixture ─────────────────────────────────────────

    private static VanSalesCustomerAuthSettings Settings => new() { DefaultCountryCode = "+263" };

    /// <summary>
    /// Onboards with a password by default, because a new sign-in requires one.
    /// </summary>
    /// <remarks>
    /// The cases here are about which shop a number belongs to and whether an account can be
    /// reinstated, none of which the password takes part in. What it does take part in is covered by
    /// <c>VanSalesCustomerPasswordSignInTests</c>.
    /// </remarks>
    private async Task<ErrorOr.ErrorOr<VanSalesCustomerAccountResult>> OnboardAsync(
        int routeCustomerId,
        string phone,
        string? displayName = "Owner",
        string? password = "shop-password")
    {
        var handler = new OnboardVanSalesCustomerAccountHandler(
            _context,
            new NoOpAuditService(),
            Options.Create(Settings),
            NullLogger<OnboardVanSalesCustomerAccountHandler>.Instance);

        var result = await handler.Handle(
            new OnboardVanSalesCustomerAccountCommand(routeCustomerId, phone, displayName, null, password),
            default);
        _context.ChangeTracker.Clear();
        return result;
    }

    private async Task<ErrorOr.ErrorOr<VanSalesCustomerAccountResult>> DeactivateAsync(int accountId)
    {
        var handler = new DeactivateVanSalesCustomerAccountHandler(
            _context,
            new NoOpAuditService(),
            NullLogger<DeactivateVanSalesCustomerAccountHandler>.Instance);

        var result = await handler.Handle(
            new DeactivateVanSalesCustomerAccountCommand(accountId),
            default);
        _context.ChangeTracker.Clear();
        return result;
    }

    private async Task GivenRouteCustomerAsync(int id, string code, bool active = true)
    {
        _context.RouteCustomers.Add(new RouteCustomerEntity
        {
            Id = id,
            AssignedBusinessPartnerCode = "BP-1",
            Code = code,
            Name = $"Shop {code}",
            IsActive = active
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private async Task GivenLiveSessionAsync(int accountId, string deviceId)
    {
        _context.VanSalesCustomerRefreshTokens.Add(new VanSalesCustomerRefreshTokenEntity
        {
            Id = Guid.NewGuid(),
            VanSalesCustomerAccountId = accountId,
            TokenHash = VanSalesCustomerRefreshTokenValue.Hash(VanSalesCustomerRefreshTokenValue.Generate()),
            DeviceId = deviceId,
            ExpiresAt = DateTime.UtcNow.AddDays(90)
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    /// <summary>Auditing is not what these tests are about, and a real one needs an HTTP context.</summary>
    private sealed class NoOpAuditService : IAuditService
    {
        public Task LogAsync(string action, string username, string userRole, string? entityType = null,
            string? entityId = null, string? details = null, string? endpoint = null,
            bool isSuccess = true, string? errorMessage = null)
            => Task.CompletedTask;

        public Task LogAsync(string action, string? entityType = null, string? entityId = null)
            => Task.CompletedTask;

        public Task LogAsync(string action, string? entityType, string? entityId, string? details,
            bool isSuccess, string? errorMessage = null)
            => Task.CompletedTask;
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
