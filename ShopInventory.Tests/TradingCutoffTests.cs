using ErrorOr;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Features.TradingCutoff;
using ShopInventory.Features.TradingCutoff.Commands.UpdateTradingCutoffSettings;
using ShopInventory.Features.TradingCutoff.Queries.GetTradingCutoffSettings;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The daily trading cut-off: the rule, the gate the sale handlers ask, the wording a rep or cashier is
/// shown, and the Settings round trip.
/// </summary>
/// <remarks>
/// The till sale itself is driven through the real handler in <c>CounterSapStockCheckTests</c>.
/// </remarks>
public sealed class TradingCutoffTests : IDisposable
{
    private static readonly TimeOnly Five = new(17, 0);
    private static readonly DateTime CatDay = new(2026, 11, 3);

    private static readonly Guid Rep = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Till = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid Clerk = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public TradingCutoffTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _context = new SnapshotSqliteContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // --- The rule ---

    [Theory]
    [InlineData(16, 59, false)]
    [InlineData(17, 0, true)]
    [InlineData(23, 59, true)]
    [InlineData(0, 1, false)]
    public void A_channel_that_is_on_closes_at_its_time_until_midnight(int hour, int minute, bool closed)
    {
        var decision = TradingCutoffRule.Evaluate(new(true, Five), null, CatDay.AddHours(hour).AddMinutes(minute));

        Assert.Equal(closed, decision.IsClosed);
        Assert.False(decision.ByAccount);
    }

    [Fact]
    public void A_channel_that_is_off_trades_all_day()
    {
        Assert.False(TradingCutoffRule.Evaluate(new(false, Five), null, CatDay.AddHours(22)).IsClosed);
    }

    [Fact]
    public void An_exempt_account_trades_after_its_channel_closes()
    {
        var decision = TradingCutoffRule.Evaluate(
            new(true, Five), new(TradingCutoffAccountMode.Exempt, null), CatDay.AddHours(20));

        Assert.False(decision.IsClosed);
    }

    [Fact]
    public void An_account_cut_off_at_its_own_time_closes_even_while_its_channel_is_off()
    {
        var account = new TradingCutoffAccountSetting(TradingCutoffAccountMode.CutOff, new TimeOnly(15, 30));

        Assert.False(TradingCutoffRule.Evaluate(new(false, Five), account, CatDay.AddHours(15).AddMinutes(29)).IsClosed);

        var decision = TradingCutoffRule.Evaluate(new(false, Five), account, CatDay.AddHours(15).AddMinutes(30));
        Assert.True(decision.IsClosed);
        Assert.True(decision.ByAccount);
        Assert.Equal(new TimeOnly(15, 30), decision.ClosedAt);
    }

    [Fact]
    public void An_account_cut_off_with_no_time_of_its_own_uses_its_channels()
    {
        var account = new TradingCutoffAccountSetting(TradingCutoffAccountMode.CutOff, null);

        var decision = TradingCutoffRule.Evaluate(new(false, new TimeOnly(18, 0)), account, CatDay.AddHours(18));

        Assert.True(decision.IsClosed);
        Assert.Equal(new TimeOnly(18, 0), decision.ClosedAt);
    }

    // --- Storage ---

    [Fact]
    public void A_stored_row_reads_back_as_it_was_written()
    {
        var channel = new TradingCutoffChannelSetting(true, new TimeOnly(16, 45));
        var account = new TradingCutoffAccountSetting(TradingCutoffAccountMode.CutOff, new TimeOnly(18, 0));

        Assert.Equal(channel, TradingCutoffKeys.ParseChannel(TradingCutoffKeys.SerializeChannel(channel)));
        Assert.Equal(account, TradingCutoffKeys.ParseAccount(TradingCutoffKeys.SerializeAccount(account)));
    }

    /// <summary>A setting nobody can read must not shut the counters.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"enabled\":true,\"cutoffTime\":\"late\"}")]
    public void A_channel_row_that_cannot_be_read_trades_all_day(string? value)
    {
        Assert.Equal(TradingCutoffChannelSetting.Default, TradingCutoffKeys.ParseChannel(value));
        Assert.False(TradingCutoffKeys.ParseChannel(value).Enabled);
    }

    [Theory]
    [InlineData(ApplicationRoles.TillOperator, SaleBusinesses.Shops)]
    [InlineData(ApplicationRoles.CartVendor, SaleBusinesses.Vending)]
    [InlineData(ApplicationRoles.Sales, SaleBusinesses.Vans)]
    [InlineData(ApplicationRoles.Adr, SaleBusinesses.Vans)]
    [InlineData(ApplicationRoles.Cashier, null)]
    public void An_account_is_grouped_under_the_channel_its_role_sells_in(string role, string? channel)
    {
        Assert.Equal(channel, TradingCutoffKeys.ChannelOf([role]));
    }

    // --- What the rep and the cashier are shown ---

    public static TheoryData<string, bool, string> EveryRefusal()
    {
        var data = new TheoryData<string, bool, string>();
        foreach (var channel in SaleBusinesses.All)
        foreach (var byAccount in new[] { false, true })
        foreach (var transaction in new[] { TradingCutoffGate.Sale, TradingCutoffGate.Order })
        {
            data.Add(channel, byAccount, transaction);
        }

        return data;
    }

    /// <summary>
    /// The van handset rewrites a message holding any of its keywords; this one has to reach the rep as
    /// written, and must not send them to the basket or to Invoice History.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryRefusal))]
    public void Every_refusal_reaches_the_handset_screen_as_written(string channel, bool byAccount, string transaction)
    {
        var error = Common.Errors.Errors.TradingCutoff.Closed(channel, Five, byAccount, transaction);

        Assert.Equal(ErrorType.Validation, error.Type);
        Assert.Equal($"Error: {error.Description}", HandsetSaleErrorReading.ParseSapError(error.Description));
        Assert.False(HandsetSaleErrorReading.IsDuplicateVanOrderError(error.Description));
        Assert.False(HandsetSaleErrorReading.OffersToEditTheBasket(error.Description));
    }

    [Fact]
    public void The_refusal_names_the_time_and_who_closed()
    {
        Assert.StartsWith(
            "Trading closed at 17:00 today for van sales, so this order cannot be taken.",
            Common.Errors.Errors.TradingCutoff.Closed(SaleBusinesses.Vans, Five, false, TradingCutoffGate.Order).Description);
        Assert.StartsWith(
            "Trading closed at 17:00 today for this account, so this sale cannot be taken.",
            Common.Errors.Errors.TradingCutoff.Closed(SaleBusinesses.Shops, Five, true, TradingCutoffGate.Sale).Description);
    }

    // --- The gate ---

    [Fact]
    public async Task With_nothing_saved_every_channel_trades()
    {
        var result = await Gate(At(22, 0)).CheckAsync(SaleBusinesses.Vans, Rep, TradingCutoffGate.Sale, "V1", CancellationToken.None);

        Assert.False(result.IsError);
    }

    [Fact]
    public async Task The_gate_refuses_after_the_saved_cut_off()
    {
        await SaveChannelAsync(SaleBusinesses.Vans, true, Five);

        var before = await Gate(At(16, 59)).CheckAsync(SaleBusinesses.Vans, Rep, TradingCutoffGate.Sale, "V1", CancellationToken.None);
        var after = await Gate(At(17, 0)).CheckAsync(SaleBusinesses.Vans, Rep, TradingCutoffGate.Sale, "V1", CancellationToken.None);

        Assert.False(before.IsError);
        Assert.Equal("TradingCutoff.Closed", after.FirstError.Code);
    }

    /// <summary>
    /// A sale begun at 16:59 and resent at 17:01 must reach the code that hands back its receipt. The
    /// resend question is asked only once trading is closed, so an open day costs no extra reads.
    /// </summary>
    [Fact]
    public async Task A_resend_of_a_transaction_already_held_is_let_through_after_the_cut_off()
    {
        await SaveChannelAsync(SaleBusinesses.Vans, true, Five);
        var asked = 0;

        var open = await Gate(At(16, 0)).CheckAsync(SaleBusinesses.Vans, Rep, TradingCutoffGate.Sale, "V1", CancellationToken.None,
            _ => { asked++; return Task.FromResult(true); });
        var resend = await Gate(At(17, 1)).CheckAsync(SaleBusinesses.Vans, Rep, TradingCutoffGate.Sale, "V1", CancellationToken.None,
            _ => { asked++; return Task.FromResult(true); });
        var fresh = await Gate(At(17, 1)).CheckAsync(SaleBusinesses.Vans, Rep, TradingCutoffGate.Sale, "V2", CancellationToken.None,
            _ => Task.FromResult(false));

        Assert.False(open.IsError);
        Assert.False(resend.IsError);
        Assert.Equal(1, asked);
        Assert.True(fresh.IsError);
    }

    [Fact]
    public async Task A_van_order_already_captured_is_known_as_a_resend()
    {
        Assert.False(await TradingCutoffResends.IsKnownVanSaleAsync(_context, "V-NEW", CancellationToken.None));
        Assert.False(await TradingCutoffResends.IsKnownSalesOrderAsync(_context, "V-NEW", CancellationToken.None));
        Assert.False(await TradingCutoffResends.IsKnownVanSaleAsync(_context, null, CancellationToken.None));
    }

    // --- Settings round trip ---

    [Fact]
    public async Task Saving_stores_each_channel_and_the_exceptions_and_reads_them_back()
    {
        await SeedUsersAsync();

        var saved = await SaveAsync(
            [
                new(SaleBusinesses.Shops, true, "17:00"),
                new(SaleBusinesses.Vending, false, "17:00"),
                new(SaleBusinesses.Vans, true, "17:30")
            ],
            [
                new(Rep, "Exempt", null),
                new(Till, "CutOff", "16:00"),
                new(Clerk, "FollowChannel", null)
            ],
            At(16, 30));

        Assert.False(saved.IsError);
        var settings = saved.Value;

        Assert.Equal([SaleBusinesses.Shops, SaleBusinesses.Vending, SaleBusinesses.Vans], settings.Channels.Select(c => c.Channel));
        Assert.Equal("17:30", settings.Channels.Single(c => c.Channel == SaleBusinesses.Vans).CutoffTime);
        Assert.False(settings.Channels.Single(c => c.Channel == SaleBusinesses.Vending).Enabled);

        var rep = settings.Accounts.Single(a => a.UserId == Rep);
        Assert.Equal(SaleBusinesses.Vans, rep.Channel);
        Assert.Equal("Exempt", rep.Mode);

        var till = settings.Accounts.Single(a => a.UserId == Till);
        Assert.Equal(SaleBusinesses.Shops, till.Channel);
        Assert.Equal("CutOff", till.Mode);
        Assert.Equal("16:00", till.CutoffTime);
        Assert.True(till.IsClosedNow);

        // A cashier sells in no channel and has no exception, so Settings does not list it.
        Assert.DoesNotContain(settings.Accounts, a => a.UserId == Clerk);
        Assert.Equal("admin", settings.UpdatedBy);
    }

    [Fact]
    public async Task An_account_left_out_of_a_save_follows_its_channel_again()
    {
        await SeedUsersAsync();
        await SaveAsync([new(SaleBusinesses.Vans, true, "17:00")], [new(Rep, "Exempt", null)], At(12, 0));

        var saved = await SaveAsync([new(SaleBusinesses.Vans, true, "17:00")], [], At(12, 0));

        Assert.Equal("FollowChannel", saved.Value.Accounts.Single(a => a.UserId == Rep).Mode);
        Assert.Equal(0, await _context.SystemConfigs.CountAsync(c => c.Key.StartsWith(TradingCutoffKeys.AccountKeyPrefix)));
    }

    [Fact]
    public async Task An_exception_for_an_account_that_does_not_exist_is_refused()
    {
        var saved = await SaveAsync([], [new(Guid.NewGuid(), "Exempt", null)], At(12, 0));

        Assert.Equal("TradingCutoff.AccountsNotFound", saved.FirstError.Code);
    }

    [Theory]
    [InlineData("shops", "17:00", true)]
    [InlineData("vans", "9:15", true)]
    [InlineData("vans", "00:00", false)]
    [InlineData("vans", "25:00", false)]
    [InlineData("vans", "", false)]
    [InlineData("depots", "17:00", false)]
    public void The_validator_takes_known_channels_and_real_times(string channel, string time, bool valid)
    {
        var result = new UpdateTradingCutoffSettingsValidator().Validate(
            new UpdateTradingCutoffSettingsCommand(Guid.NewGuid(), "admin", [new(channel, true, time)], []));

        Assert.Equal(valid, result.IsValid);
    }

    [Fact]
    public void The_validator_refuses_an_unknown_exception()
    {
        var result = new UpdateTradingCutoffSettingsValidator().Validate(
            new UpdateTradingCutoffSettingsCommand(Guid.NewGuid(), "admin", [], [new(Rep, "Sometimes", null)]));

        Assert.False(result.IsValid);
    }

    // --- Helpers ---

    private static FixedClock At(int hour, int minute) =>
        // CAT is UTC+2 all year.
        new(new DateTimeOffset(CatDay.AddHours(hour - 2).AddMinutes(minute), TimeSpan.Zero));

    private TradingCutoffGate Gate(TimeProvider clock) =>
        new(_context, clock, NullLogger<TradingCutoffGate>.Instance);

    private async Task SaveChannelAsync(string channel, bool enabled, TimeOnly time)
    {
        await TradingCutoffKeys.StageAsync(_context, TradingCutoffKeys.ChannelKey(channel), "json",
            TradingCutoffKeys.SerializeChannel(new(enabled, time)), "test", CancellationToken.None);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    [Fact]
    public async Task A_vending_account_is_described_by_its_depots_business_partner_name()
    {
        await SeedUsersAsync();
        var cart = Guid.NewGuid();
        _context.Users.Add(new User
        {
            Id = cart, Username = "byo-cart", PasswordHash = "x", Role = ApplicationRoles.CartVendor,
            AssignedBusinessPartnerCode = "COR008", AssignedWarehouseCodes = "[\"KEFBYC\"]"
        });
        _context.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = "VEND-1", SourceSystem = SaleSourceSystems.Vending, CardCode = "COR008",
            CardName = "Cortina Bulawayo Vending", WarehouseCode = "KEFBYC", DocDate = CatDay, Currency = "USD",
            TotalAmount = 5m, AmountPaid = 5m, CreatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var settings = await new GetTradingCutoffSettingsHandler(_context, At(12, 0))
            .Handle(new GetTradingCutoffSettingsQuery(), CancellationToken.None);

        Assert.False(settings.IsError);
        Assert.Equal("Cortina Bulawayo Vending", settings.Value.Accounts.Single(a => a.UserId == cart).Detail);
        // A van account is not a vending depot and keeps its code.
        Assert.Equal("VAN004", settings.Value.Accounts.Single(a => a.UserId == Rep).Detail);
    }

    private async Task SeedUsersAsync()
    {
        _context.Users.AddRange(
            new User { Id = Rep, Username = "van004-a", PasswordHash = "x", Role = ApplicationRoles.Sales, AssignedBusinessPartnerCode = "VAN004" },
            new User { Id = Till, Username = "kefshop-till", PasswordHash = "x", Role = ApplicationRoles.TillOperator },
            new User { Id = Clerk, Username = "clerk", PasswordHash = "x", Role = ApplicationRoles.Cashier });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private async Task<ErrorOr<TradingCutoffSettings>> SaveAsync(
        IReadOnlyList<TradingCutoffChannelInput> channels,
        IReadOnlyList<TradingCutoffAccountInput> accounts,
        TimeProvider clock)
    {
        var query = new GetTradingCutoffSettingsHandler(_context, clock);
        var mediator = StubProxy.For<IMediator>((method, args) => args![0] switch
        {
            GetTradingCutoffSettingsQuery request => query.Handle(request, CancellationToken.None),
            var other => throw new InvalidOperationException($"No answer for {other?.GetType().Name}")
        });

        var result = await new UpdateTradingCutoffSettingsHandler(
                _context,
                mediator,
                StubProxy.For<IAuditService>((_, _) => Task.CompletedTask),
                NullLogger<UpdateTradingCutoffSettingsHandler>.Instance)
            .Handle(new UpdateTradingCutoffSettingsCommand(Guid.NewGuid(), "admin", channels, accounts), CancellationToken.None);

        _context.ChangeTracker.Clear();
        return result;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
