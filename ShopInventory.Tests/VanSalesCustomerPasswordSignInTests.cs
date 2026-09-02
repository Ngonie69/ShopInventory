using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.VanSalesCustomerAuth;
using ShopInventory.Features.VanSalesCustomerAuth.Commands.OnboardVanSalesCustomerAccount;
using ShopInventory.Features.VanSalesCustomerAuth.Commands.SignInVanSalesCustomer;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The password sign-in the ordering app uses, which is unauthenticated and reachable by anyone.
///
/// A password is not a one-time code: it does not expire, so an attacker gets as many attempts as
/// the account lockout allows and no natural deadline. Two properties carry most of the weight, and
/// neither shows up when reading the happy path. The first is that a number with no account, a
/// number whose account has no password, and a wrong password are one answer — otherwise sign-in
/// becomes the customer-list oracle the code endpoint refuses to be, and worse, names the accounts
/// with no password on them. The second is that failures are capped on the account rather than only
/// on the endpoint, because attempts arriving from many addresses defeat a rate limiter that
/// partitions by caller but all land on one account.
///
/// Both are properties of things the code declines to do, which is what a later refactor drops
/// without noticing.
/// </summary>
public sealed class VanSalesCustomerPasswordSignInTests : IDisposable
{
    private const string TypedNumber = "0771234567";
    private const string StoredNumber = "+263771234567";
    private const string Password = "shop-password";
    private const string WrongPassword = "not-the-password";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public VanSalesCustomerPasswordSignInTests()
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

    // ── Signing in ──────────────────────────────────────

    [Fact]
    public async Task The_right_password_returns_a_session()
    {
        await GivenCustomerAsync();

        var result = await SignInAsync(TypedNumber, Password);

        Assert.False(result.IsError);
        Assert.False(string.IsNullOrWhiteSpace(result.Value.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(result.Value.RefreshToken));
        Assert.Equal("CUST-1", result.Value.Customer.CustomerCode);
    }

    [Fact]
    public async Task The_number_may_be_typed_the_way_a_shopkeeper_says_it()
    {
        await GivenCustomerAsync();

        var local = await SignInAsync("0771234567", Password);
        var international = await SignInAsync("+263 77 123 4567", Password);

        Assert.False(local.IsError);
        Assert.False(international.IsError);
    }

    [Fact]
    public async Task A_wrong_password_is_refused()
    {
        await GivenCustomerAsync();

        var result = await SignInAsync(TypedNumber, WrongPassword);

        Assert.True(result.IsError);
        Assert.Equal(Errors.VanSalesCustomerAuth.InvalidCredentials.Code, result.FirstError.Code);
    }

    [Fact]
    public async Task An_unregistered_number_is_refused_exactly_as_a_wrong_password_is()
    {
        // The assertion that matters. If these two ever differ, sign-in answers "is this shop your
        // customer?" to anyone willing to try numbers.
        await GivenCustomerAsync();

        var wrongPassword = await SignInAsync(TypedNumber, WrongPassword);
        var unregistered = await SignInAsync("0779999999", WrongPassword);

        Assert.True(wrongPassword.IsError);
        Assert.True(unregistered.IsError);
        Assert.Equal(wrongPassword.FirstError.Code, unregistered.FirstError.Code);
        Assert.Equal(wrongPassword.FirstError.Description, unregistered.FirstError.Description);
    }

    [Fact]
    public async Task An_account_with_no_password_is_refused_exactly_as_a_wrong_password_is()
    {
        // Accounts predating passwords have none. Saying so would hand an attacker the list of
        // accounts that cannot be signed into but also cannot refuse a reset request convincingly.
        await GivenCustomerAsync(password: null);

        var noPassword = await SignInAsync(TypedNumber, "anything at all");

        await GivenCustomerAsync("+263772222222", "CUST-2", 2);
        var wrongPassword = await SignInAsync("0772222222", WrongPassword);

        Assert.True(noPassword.IsError);
        Assert.Equal(wrongPassword.FirstError.Code, noPassword.FirstError.Code);
        Assert.Equal(wrongPassword.FirstError.Description, noPassword.FirstError.Description);
    }

    [Fact]
    public async Task An_empty_password_never_matches_an_account_that_has_none()
    {
        // The null-hash case written the way it would actually be probed: a blank password against
        // an account with a blank hash must not compare equal.
        await GivenCustomerAsync(password: null);

        var result = await SignInAsync(TypedNumber, string.Empty);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task A_deactivated_account_cannot_sign_in_with_the_right_password()
    {
        await GivenCustomerAsync(accountActive: false);

        var result = await SignInAsync(TypedNumber, Password);

        Assert.True(result.IsError);
        Assert.Equal(Errors.VanSalesCustomerAuth.AccountInactive.Code, result.FirstError.Code);
    }

    [Fact]
    public async Task Something_that_is_not_a_phone_number_is_refused_before_anything_is_looked_up()
    {
        var result = await SignInAsync("not a number", Password);

        Assert.True(result.IsError);
        Assert.Equal(Errors.VanSalesCustomerAuth.InvalidPhoneNumber.Code, result.FirstError.Code);
    }

    // ── The attempt ceiling ─────────────────────────────

    [Fact]
    public async Task Repeated_wrong_passwords_lock_the_account()
    {
        await GivenCustomerAsync();

        for (var attempt = 0; attempt < 10; attempt++)
        {
            await SignInAsync(TypedNumber, WrongPassword);
        }

        var account = await _context.VanSalesCustomerAccounts.SingleAsync();
        Assert.NotNull(account.LockedUntil);
    }

    [Fact]
    public async Task A_locked_account_refuses_even_the_right_password()
    {
        await GivenCustomerAsync();
        await LockAsync();

        var result = await SignInAsync(TypedNumber, Password);

        Assert.True(result.IsError);
        Assert.Equal(Errors.VanSalesCustomerAuth.TooManyAttempts.Code, result.FirstError.Code);
    }

    [Fact]
    public async Task Passwords_and_codes_spend_the_same_attempt_budget()
    {
        // One counter for both credentials, not one each. Two budgets would let an attacker use
        // whichever endpoint still had attempts left on it.
        await GivenCustomerAsync();
        await GivenPriorFailuresAsync(9);

        await SignInAsync(TypedNumber, WrongPassword);

        var account = await _context.VanSalesCustomerAccounts.SingleAsync();
        Assert.NotNull(account.LockedUntil);
    }

    [Fact]
    public async Task Signing_in_clears_the_failures_behind_it()
    {
        await GivenCustomerAsync();
        await GivenPriorFailuresAsync(5);

        var result = await SignInAsync(TypedNumber, Password);

        Assert.False(result.IsError);

        var account = await _context.VanSalesCustomerAccounts.SingleAsync();
        Assert.Equal(0, account.FailedOtpCount);
        Assert.Null(account.LockedUntil);
    }

    [Fact]
    public async Task Attempts_against_an_unregistered_number_leave_nothing_behind()
    {
        // Nothing to count against, and nothing that could later be read back to say the number was
        // tried — which would be its own answer to "does this account exist".
        await GivenCustomerAsync();

        await SignInAsync("0779999999", WrongPassword);

        var account = await _context.VanSalesCustomerAccounts.SingleAsync();
        Assert.Equal(0, account.FailedOtpCount);
        Assert.Null(account.LockedUntil);
    }

    // ── Setting the password ────────────────────────────

    [Fact]
    public async Task Onboarding_stores_a_hash_and_not_the_password()
    {
        await GivenRouteCustomerAsync();

        await OnboardAsync(password: Password);

        var account = await _context.VanSalesCustomerAccounts.SingleAsync();
        Assert.NotNull(account.PasswordHash);
        Assert.DoesNotContain(Password, account.PasswordHash);
        Assert.NotNull(account.PasswordSetAt);
    }

    [Fact]
    public async Task A_new_sign_in_without_a_password_is_refused()
    {
        // An account created with no password would sit in the operator's list looking finished and
        // refuse the shop with "those details do not match".
        await GivenRouteCustomerAsync();

        var result = await OnboardAsync(password: null);

        Assert.True(result.IsError);
        Assert.Equal(Errors.VanSalesCustomerAuth.PasswordRequired.Code, result.FirstError.Code);
        Assert.Empty(await _context.VanSalesCustomerAccounts.ToListAsync());
    }

    [Fact]
    public async Task The_password_set_at_onboarding_signs_the_shop_in()
    {
        await GivenRouteCustomerAsync();
        await OnboardAsync(password: Password);

        var result = await SignInAsync(TypedNumber, Password);

        Assert.False(result.IsError);
    }

    [Fact]
    public async Task Re_onboarding_without_a_password_keeps_the_one_already_set()
    {
        // Re-onboarding is mostly moving a shop to a new handset. Clearing the password there would
        // sign out an app that was working, with nothing on any screen to say why.
        await GivenRouteCustomerAsync();
        await OnboardAsync(password: Password);

        await OnboardAsync(password: null);

        var result = await SignInAsync(TypedNumber, Password);
        Assert.False(result.IsError);
    }

    [Fact]
    public async Task Re_onboarding_with_a_new_password_replaces_the_old_one()
    {
        // Which is how a forgotten password is reset: the rep is in the shop either way.
        await GivenRouteCustomerAsync();
        await OnboardAsync(password: Password);

        await OnboardAsync(password: "a-different-password");

        var oldOne = await SignInAsync(TypedNumber, Password);
        var newOne = await SignInAsync(TypedNumber, "a-different-password");

        Assert.True(oldOne.IsError);
        Assert.False(newOne.IsError);
    }

    [Fact]
    public async Task Two_shops_with_the_same_password_do_not_share_a_hash()
    {
        // BCrypt salts per row. Without that, one stolen table plus one known password names every
        // other account using it.
        await GivenCustomerAsync();
        await GivenCustomerAsync("+263772222222", "CUST-2", 2);

        var hashes = await _context.VanSalesCustomerAccounts
            .Select(a => a.PasswordHash)
            .ToListAsync();

        Assert.Equal(2, hashes.Count);
        Assert.NotEqual(hashes[0], hashes[1]);
    }

    // ── Fixture ─────────────────────────────────────────

    private static VanSalesCustomerAuthSettings Settings => new()
    {
        DefaultCountryCode = "+263",
        MaxConsecutiveFailuresBeforeLockout = 10,
        LockoutMinutes = 30,
        AccessTokenExpirationMinutes = 30,
        RefreshTokenExpirationDays = 90
    };

    private static JwtSettings Jwt => new()
    {
        SecretKey = "test-signing-key-that-is-long-enough-for-hs256",
        Issuer = "test-issuer",
        Audience = "test-audience"
    };

    private async Task<ErrorOr.ErrorOr<VanSalesCustomerSessionResult>> SignInAsync(
        string phone,
        string? password)
    {
        var handler = new SignInVanSalesCustomerHandler(
            _context,
            new VanSalesCustomerSessionIssuer(
                _context,
                new VanSalesCustomerTokenIssuer(Options.Create(Jwt), Options.Create(Settings)),
                Options.Create(Settings),
                NullLogger<VanSalesCustomerSessionIssuer>.Instance),
            Options.Create(Settings),
            NullLogger<SignInVanSalesCustomerHandler>.Instance);

        var result = await handler.Handle(
            new SignInVanSalesCustomerCommand(phone, password, "device-1", "Test Handset", "127.0.0.1"),
            default);
        _context.ChangeTracker.Clear();
        return result;
    }

    private async Task<ErrorOr.ErrorOr<VanSalesCustomerAccountResult>> OnboardAsync(string? password)
    {
        var handler = new OnboardVanSalesCustomerAccountHandler(
            _context,
            new NoOpAuditService(),
            Options.Create(Settings),
            NullLogger<OnboardVanSalesCustomerAccountHandler>.Instance);

        var result = await handler.Handle(
            new OnboardVanSalesCustomerAccountCommand(1, TypedNumber, "Owner", null, password),
            default);
        _context.ChangeTracker.Clear();
        return result;
    }

    private async Task GivenRouteCustomerAsync(
        string phone = StoredNumber,
        string code = "CUST-1",
        int routeCustomerId = 1)
    {
        _context.RouteCustomers.Add(new RouteCustomerEntity
        {
            Id = routeCustomerId,
            AssignedBusinessPartnerCode = $"BP-{routeCustomerId}",
            Code = code,
            Name = $"Shop {code}",
            Phone = phone,
            IsActive = true
        });

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    /// <summary>A shop with a sign-in, hashed the way onboarding hashes it.</summary>
    private async Task GivenCustomerAsync(
        string phone = StoredNumber,
        string code = "CUST-1",
        int routeCustomerId = 1,
        bool accountActive = true,
        string? password = Password)
    {
        await GivenRouteCustomerAsync(phone, code, routeCustomerId);

        _context.VanSalesCustomerAccounts.Add(new VanSalesCustomerAccountEntity
        {
            RouteCustomerId = routeCustomerId,
            PhoneE164 = phone,
            DisplayName = "Owner",
            IsActive = accountActive,
            PasswordHash = password is null ? null : VanSalesCustomerPassword.Hash(password),
            PasswordSetAt = password is null ? null : DateTime.UtcNow
        });

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private async Task GivenPriorFailuresAsync(int count)
    {
        await _context.VanSalesCustomerAccounts.ExecuteUpdateAsync(
            s => s.SetProperty(a => a.FailedOtpCount, count));
        _context.ChangeTracker.Clear();
    }

    private async Task LockAsync()
    {
        await _context.VanSalesCustomerAccounts.ExecuteUpdateAsync(
            s => s.SetProperty(a => a.LockedUntil, DateTime.UtcNow.AddMinutes(30)));
        _context.ChangeTracker.Clear();
    }

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
    /// <see cref="DailyStockSnapshotItemEntity.Version"/> is <c>[Timestamp]</c>, which Npgsql maps to
    /// the store-generated <c>xmin</c>. SQLite has no equivalent, EF leaves it out of the INSERT, and
    /// the NOT NULL constraint from EnsureCreated then fails. Nothing here touches that entity.
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
