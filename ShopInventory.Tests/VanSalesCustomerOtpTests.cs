using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.VanSalesCustomerAuth;
using ShopInventory.Features.VanSalesCustomerAuth.Commands.RequestVanSalesCustomerOtp;
using ShopInventory.Features.VanSalesCustomerAuth.Commands.VerifyVanSalesCustomerOtp;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// The van sales customer sign-in, which is unauthenticated, reachable by anyone, and takes a phone
/// number.
///
/// Two properties are worth more than the rest, and neither is visible by reading the happy path.
/// The first is that requesting a code must be indistinguishable for a number that trades with us
/// and one that does not — otherwise the endpoint answers "is this shop your customer?" to whoever
/// asks, and a competitor can read a route's customer list off it one number at a time. The second
/// is that a six-digit code is guessable, so what protects an account is the ceiling on attempts
/// rather than the code itself.
///
/// Both are properties of things the code does <em>not</em> do, which is exactly what a later
/// refactor drops without noticing.
/// </summary>
public sealed class VanSalesCustomerOtpTests : IDisposable
{
    private const string TypedNumber = "0771234567";
    private const string StoredNumber = "+263771234567";
    private const string Secret = "test-signing-key-that-is-long-enough-for-hs256";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly RecordingOtpSender _sender = new();

    public VanSalesCustomerOtpTests()
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

    // ── Requesting a code ───────────────────────────────

    [Fact]
    public async Task A_registered_number_is_sent_a_code()
    {
        await GivenCustomerAsync();

        var result = await RequestAsync(TypedNumber);

        Assert.False(result.IsError);
        Assert.Single(_sender.Sent);
        Assert.Equal(StoredNumber, _sender.Sent[0].Phone);
    }

    [Fact]
    public async Task An_unregistered_number_is_answered_exactly_as_a_registered_one_is()
    {
        // The assertion that matters. If these two responses ever differ, the endpoint becomes a
        // customer-list oracle for anyone willing to try numbers.
        await GivenCustomerAsync();

        var registered = await RequestAsync(TypedNumber);
        _sender.Sent.Clear();
        var unregistered = await RequestAsync("0779999999");

        Assert.False(unregistered.IsError);
        Assert.Equal(registered.Value, unregistered.Value);

        // And nothing was sent, so probing costs us no messages either.
        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task A_deactivated_account_is_answered_the_same_way_and_sent_nothing()
    {
        await GivenCustomerAsync(accountActive: false);

        var result = await RequestAsync(TypedNumber);

        Assert.False(result.IsError);
        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task Asking_again_straight_away_sends_nothing_but_answers_the_same()
    {
        // Without a cooldown this endpoint can make a customer's phone buzz all night. It has to be
        // silent: a "too soon" error is only ever returned for a number that already has a live
        // code, which is to say a registered one.
        await GivenCustomerAsync();

        var first = await RequestAsync(TypedNumber);
        var second = await RequestAsync(TypedNumber);

        Assert.Equal(first.Value, second.Value);
        Assert.Single(_sender.Sent);
    }

    [Fact]
    public async Task A_new_code_retires_the_previous_one()
    {
        // A customer who taps resend must not end up holding two live codes with no way to tell
        // which one the app will take.
        await GivenCustomerAsync();
        await RequestAsync(TypedNumber);
        var firstCode = _sender.Sent[0].Code;

        await ExpireCooldownAsync();
        await RequestAsync(TypedNumber);

        var verifyOld = await VerifyAsync(TypedNumber, firstCode);
        Assert.True(verifyOld.IsError);

        var verifyNew = await VerifyAsync(TypedNumber, _sender.Sent[1].Code);
        Assert.False(verifyNew.IsError);
    }

    [Fact]
    public async Task Nonsense_is_refused_because_that_reveals_nothing()
    {
        var result = await RequestAsync("not a phone number");

        Assert.True(result.IsError);
        Assert.Equal("VanSalesCustomerAuth.InvalidPhoneNumber", result.FirstError.Code);
    }

    // ── Verifying a code ────────────────────────────────

    [Fact]
    public async Task The_right_code_returns_a_session_for_the_right_customer()
    {
        await GivenCustomerAsync();
        await RequestAsync(TypedNumber);

        var result = await VerifyAsync(TypedNumber, _sender.Sent[0].Code);

        Assert.False(result.IsError);
        Assert.NotEmpty(result.Value.AccessToken);
        Assert.NotEmpty(result.Value.RefreshToken);
        Assert.Equal("CUST-1", result.Value.Customer.CustomerCode);
    }

    [Fact]
    public async Task A_code_signs_in_once_and_never_again()
    {
        await GivenCustomerAsync();
        await RequestAsync(TypedNumber);
        var code = _sender.Sent[0].Code;

        Assert.False((await VerifyAsync(TypedNumber, code)).IsError);

        // Replay. A code that still worked would make interception of one message permanent.
        var replay = await VerifyAsync(TypedNumber, code);
        Assert.True(replay.IsError);
        Assert.Equal("VanSalesCustomerAuth.InvalidCode", replay.FirstError.Code);
    }

    [Fact]
    public async Task An_expired_code_is_refused()
    {
        await GivenCustomerAsync();
        await RequestAsync(TypedNumber);
        var code = _sender.Sent[0].Code;

        await _context.VanSalesCustomerOtps
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));

        var result = await VerifyAsync(TypedNumber, code);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task A_wrong_code_and_an_unissued_one_are_reported_identically()
    {
        // Grading a guess — "expired" for one, "incorrect" for another — tells an attacker their
        // search is in the right place.
        await GivenCustomerAsync();
        await RequestAsync(TypedNumber);

        var wrong = await VerifyAsync(TypedNumber, WrongCode(_sender.Sent[0].Code));
        var never = await VerifyAsync("0779999999", "123456");

        Assert.True(wrong.IsError);
        Assert.True(never.IsError);
        Assert.Equal(never.FirstError.Code, wrong.FirstError.Code);
    }

    [Fact]
    public async Task A_code_is_spent_once_its_attempts_run_out()
    {
        await GivenCustomerAsync();
        await RequestAsync(TypedNumber);
        var code = _sender.Sent[0].Code;
        var wrong = WrongCode(code);

        for (var attempt = 0; attempt < Settings.MaxOtpAttempts; attempt++)
        {
            Assert.True((await VerifyAsync(TypedNumber, wrong)).IsError);
        }

        // The real code is dead too: guessing burns the code, so an exhausted one cannot then work
        // for whoever happens to guess last.
        var result = await VerifyAsync(TypedNumber, code);
        Assert.True(result.IsError);
        Assert.Equal("VanSalesCustomerAuth.TooManyAttempts", result.FirstError.Code);
    }

    [Fact]
    public async Task Repeated_failures_lock_the_account_itself()
    {
        // The endpoint limiter partitions by caller, so attempts spread across addresses walk past
        // it. They all land on one account, which is where the ceiling has to be.
        await GivenCustomerAsync();

        for (var round = 0; round < Settings.MaxConsecutiveFailuresBeforeLockout; round++)
        {
            await ExpireCooldownAsync();
            await RequestAsync(TypedNumber);
            await VerifyAsync(TypedNumber, WrongCode(_sender.Sent[^1].Code));
        }

        var account = await _context.VanSalesCustomerAccounts.AsNoTracking().SingleAsync();
        Assert.NotNull(account.LockedUntil);
        Assert.True(account.LockedUntil > DateTime.UtcNow);

        var sentBefore = _sender.Sent.Count;
        await ExpireCooldownAsync();
        await RequestAsync(TypedNumber);

        // A locked account is sent nothing, so a lockout cannot be turned into a way to spam a phone.
        Assert.Equal(sentBefore, _sender.Sent.Count);
    }

    [Fact]
    public async Task Signing_in_clears_the_failure_count()
    {
        await GivenCustomerAsync();
        await RequestAsync(TypedNumber);
        await VerifyAsync(TypedNumber, WrongCode(_sender.Sent[0].Code));

        Assert.Equal(1, (await _context.VanSalesCustomerAccounts.AsNoTracking().SingleAsync()).FailedOtpCount);

        await ExpireCooldownAsync();
        await RequestAsync(TypedNumber);
        Assert.False((await VerifyAsync(TypedNumber, _sender.Sent[^1].Code)).IsError);

        var account = await _context.VanSalesCustomerAccounts.AsNoTracking().SingleAsync();
        Assert.Equal(0, account.FailedOtpCount);
        Assert.NotNull(account.LastLoginAt);
    }

    [Fact]
    public async Task The_stored_code_is_not_the_code()
    {
        // A table of live codes makes one database read equivalent to every customer's password.
        await GivenCustomerAsync();
        await RequestAsync(TypedNumber);

        var stored = await _context.VanSalesCustomerOtps.AsNoTracking().SingleAsync();

        Assert.DoesNotContain(_sender.Sent[0].Code, stored.CodeHash, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_code_issued_for_one_number_cannot_be_used_by_another()
    {
        // The phone is bound into the hash, so a row lifted between numbers verifies for neither.
        await GivenCustomerAsync();
        await GivenCustomerAsync("+263772222222", "CUST-2", 2);
        await RequestAsync(TypedNumber);
        var code = _sender.Sent[0].Code;

        var result = await VerifyAsync("0772222222", code);

        Assert.True(result.IsError);
    }

    // ── Fixture ─────────────────────────────────────────

    private static VanSalesCustomerAuthSettings Settings => new()
    {
        DefaultCountryCode = "+263",
        OtpLength = 6,
        OtpTtlMinutes = 5,
        MaxOtpAttempts = 5,
        MaxConsecutiveFailuresBeforeLockout = 10,
        LockoutMinutes = 30,
        ResendCooldownSeconds = 60,
        AccessTokenExpirationMinutes = 30,
        RefreshTokenExpirationDays = 90
    };

    private static JwtSettings Jwt => new()
    {
        SecretKey = Secret,
        Issuer = "test-issuer",
        Audience = "test-audience"
    };

    private static string WrongCode(string code) => code == "000000" ? "111111" : "000000";

    private async Task<ErrorOr.ErrorOr<RequestVanSalesCustomerOtpResult>> RequestAsync(string phone)
    {
        var handler = new RequestVanSalesCustomerOtpHandler(
            _context,
            _sender,
            Options.Create(Settings),
            Options.Create(Jwt),
            NullLogger<RequestVanSalesCustomerOtpHandler>.Instance);

        var result = await handler.Handle(new RequestVanSalesCustomerOtpCommand(phone, "127.0.0.1"), default);
        _context.ChangeTracker.Clear();
        return result;
    }

    private async Task<ErrorOr.ErrorOr<VanSalesCustomerSessionResult>> VerifyAsync(string phone, string code)
    {
        var handler = new VerifyVanSalesCustomerOtpHandler(
            _context,
            new VanSalesCustomerSessionIssuer(
                _context,
                new VanSalesCustomerTokenIssuer(Options.Create(Jwt), Options.Create(Settings)),
                Options.Create(Settings),
                NullLogger<VanSalesCustomerSessionIssuer>.Instance),
            Options.Create(Settings),
            Options.Create(Jwt),
            NullLogger<VerifyVanSalesCustomerOtpHandler>.Instance);

        var result = await handler.Handle(
            new VerifyVanSalesCustomerOtpCommand(phone, code, "device-1", "Test Handset", "127.0.0.1"),
            default);
        _context.ChangeTracker.Clear();
        return result;
    }

    /// <summary>Push every existing code out of the resend cooldown so the next request sends.</summary>
    private async Task ExpireCooldownAsync()
    {
        await _context.VanSalesCustomerOtps.ExecuteUpdateAsync(
            s => s.SetProperty(o => o.CreatedAt, DateTime.UtcNow.AddHours(-1)));
        _context.ChangeTracker.Clear();
    }

    private async Task GivenCustomerAsync(
        string phone = StoredNumber,
        string code = "CUST-1",
        int routeCustomerId = 1,
        bool accountActive = true)
    {
        var routeCustomer = new RouteCustomerEntity
        {
            Id = routeCustomerId,
            AssignedBusinessPartnerCode = "BP-1",
            Code = code,
            Name = $"Shop {code}",
            Phone = phone,
            IsActive = true
        };
        _context.RouteCustomers.Add(routeCustomer);
        await _context.SaveChangesAsync();

        _context.VanSalesCustomerAccounts.Add(new VanSalesCustomerAccountEntity
        {
            RouteCustomerId = routeCustomer.Id,
            PhoneE164 = phone,
            DisplayName = "Owner",
            IsActive = accountActive
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private sealed class RecordingOtpSender : IVanSalesCustomerOtpSender
    {
        public List<(string Phone, string Code)> Sent { get; } = [];

        public Task<OtpDeliveryChannel> SendAsync(string phoneE164, string code, CancellationToken cancellationToken)
        {
            Sent.Add((phoneE164, code));
            return Task.FromResult(OtpDeliveryChannel.WhatsApp);
        }
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
