using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.VanSalesOrders;
using ShopInventory.Features.VanSalesOrders.Commands.SubmitVanSalesCustomerOrder;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Placing an order from a phone that may have no signal.
///
/// The whole offline design rests on one property: submitting the same <c>ClientRequestId</c> twice
/// produces one order. It has to, because a van sales customer orders from places where the reply
/// never arrives, and a lost reply is indistinguishable from a request that never landed. The app
/// therefore retries — and if a retry created a second order, the shop would be delivered twice and
/// invoiced twice, and the first anyone would know is the rep arguing at the door.
///
/// The key is minted when the draft is created rather than when it is sent, which is what makes
/// every retry carry the same one.
/// </summary>
public sealed class VanSalesOrderIdempotencyTests : IDisposable
{
    private const string Key = "11111111-2222-3333-4444-555555555555";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly StubPriceCatalog _prices = new();

    public VanSalesOrderIdempotencyTests()
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

    // ── Idempotency ─────────────────────────────────────

    [Fact]
    public async Task The_same_key_sent_twice_produces_one_order()
    {
        var accountId = await GivenCustomerAsync();

        var first = await SubmitAsync(accountId, Key, ("FRM001", 5m));
        var second = await SubmitAsync(accountId, Key, ("FRM001", 5m));

        Assert.False(first.IsError);
        Assert.False(second.IsError);
        Assert.Equal(first.Value.Id, second.Value.Id);
        Assert.Equal(first.Value.OrderNumber, second.Value.OrderNumber);
        Assert.Equal(1, await _context.VanSalesOrders.CountAsync());
    }

    [Fact]
    public async Task A_replay_returns_the_original_rather_than_an_error()
    {
        // A handset retrying is not in error. Reporting a conflict would leave the app believing
        // the order never went through, and it would keep trying.
        var accountId = await GivenCustomerAsync();
        await SubmitAsync(accountId, Key, ("FRM001", 5m));

        var replay = await SubmitAsync(accountId, Key, ("FRM001", 5m));

        Assert.False(replay.IsError);
    }

    [Fact]
    public async Task A_replay_carrying_different_lines_still_returns_the_original()
    {
        // The key identifies the order, not the payload. An edited retry must not quietly replace
        // what was already accepted and possibly already picked.
        var accountId = await GivenCustomerAsync();
        var first = await SubmitAsync(accountId, Key, ("FRM001", 5m));

        var replay = await SubmitAsync(accountId, Key, ("FRM001", 500m), ("FRM002", 99m));

        Assert.Equal(first.Value.Id, replay.Value.Id);
        Assert.Equal(5m, replay.Value.Lines.Single().QuantityOrdered);
        Assert.Equal(1, await _context.VanSalesOrders.CountAsync());
    }

    [Fact]
    public async Task Different_keys_produce_different_orders()
    {
        // The other half: a customer who genuinely orders twice gets two orders.
        var accountId = await GivenCustomerAsync();

        await SubmitAsync(accountId, Key, ("FRM001", 5m));
        await SubmitAsync(accountId, "99999999-8888-7777-6666-555555555555", ("FRM001", 5m));

        Assert.Equal(2, await _context.VanSalesOrders.CountAsync());
    }

    [Fact]
    public async Task Order_numbers_do_not_repeat_within_a_day()
    {
        var accountId = await GivenCustomerAsync();

        await SubmitAsync(accountId, "key-a", ("FRM001", 1m));
        await SubmitAsync(accountId, "key-b", ("FRM001", 1m));
        await SubmitAsync(accountId, "key-c", ("FRM001", 1m));

        var numbers = await _context.VanSalesOrders.AsNoTracking()
            .Select(o => o.OrderNumber)
            .ToListAsync();

        Assert.Equal(3, numbers.Distinct().Count());
        Assert.All(numbers, n => Assert.StartsWith("VSO-", n));
    }

    [Fact]
    public async Task The_tenth_order_of_the_day_sorts_after_the_ninth()
    {
        // Ordering by string alone would put 0010 between 0001 and 0002 and hand out 0002 again.
        var accountId = await GivenCustomerAsync();

        for (var i = 0; i < 11; i++)
        {
            await SubmitAsync(accountId, $"key-{i}", ("FRM001", 1m));
        }

        var numbers = await _context.VanSalesOrders.AsNoTracking()
            .Select(o => o.OrderNumber)
            .ToListAsync();

        Assert.Equal(11, numbers.Distinct().Count());
        Assert.Contains(numbers, n => n.EndsWith("-0011"));
    }

    [Fact]
    public async Task The_key_is_echoed_back_so_the_app_can_match_it_to_its_draft()
    {
        var accountId = await GivenCustomerAsync();

        var result = await SubmitAsync(accountId, Key, ("FRM001", 5m));

        Assert.Equal(Key, result.Value.ClientRequestId);
    }

    // ── Pricing and totals ──────────────────────────────

    [Fact]
    public async Task The_server_prices_the_order_not_the_handset()
    {
        // The app caches a catalogue that can be days old. Nothing in the request carries a price.
        var accountId = await GivenCustomerAsync();

        var result = await SubmitAsync(accountId, Key, ("FRM001", 3m));

        var line = result.Value.Lines.Single();
        Assert.Equal(2.00m, line.UnitPrice);
        Assert.Equal(6.00m, line.LineTotal);
    }

    [Fact]
    public async Task Totals_add_up_and_carry_tax()
    {
        var accountId = await GivenCustomerAsync();

        var result = await SubmitAsync(accountId, Key, ("FRM001", 3m), ("FRM002", 2m));

        Assert.Equal(12.00m, result.Value.SubTotal);   // 3 x 2.00 + 2 x 3.00
        Assert.Equal(1.86m, result.Value.TaxAmount);   // 15.5% of each line, rounded per line
        Assert.Equal(13.86m, result.Value.DocTotal);
    }

    [Fact]
    public async Task The_same_item_added_twice_becomes_one_line()
    {
        // A shopkeeper tapping a product twice means more of it, not two lines for the picker to
        // reconcile at the depot.
        var accountId = await GivenCustomerAsync();

        var result = await SubmitAsync(accountId, Key, ("FRM001", 2m), ("FRM001", 3m));

        var line = result.Value.Lines.Single();
        Assert.Equal(5m, line.QuantityOrdered);
    }

    [Fact]
    public async Task Lines_start_with_nothing_delivered()
    {
        var accountId = await GivenCustomerAsync();

        var result = await SubmitAsync(accountId, Key, ("FRM001", 5m));

        Assert.Equal(0m, result.Value.Lines.Single().QuantityFulfilled);
    }

    [Fact]
    public async Task An_order_arrives_accepted()
    {
        // Auto-accept was the decision: the rep adjusts at delivery rather than someone approving
        // each order.
        var accountId = await GivenCustomerAsync();

        var result = await SubmitAsync(accountId, Key, ("FRM001", 5m));

        Assert.Equal(VanSalesOrderStatus.Accepted, result.Value.Status);
    }

    // ── What is refused ─────────────────────────────────

    [Fact]
    public async Task An_item_no_longer_on_the_catalogue_is_refused_by_name()
    {
        // A queued order can arrive days after it was built. The app has to tell the shopkeeper
        // which line to remove, so the refusal names the items.
        var accountId = await GivenCustomerAsync();

        var result = await SubmitAsync(accountId, Key, ("FRM001", 1m), ("GONE-1", 1m));

        Assert.True(result.IsError);
        Assert.Equal("VanSalesOrders.UnavailableItems", result.FirstError.Code);
        Assert.Contains("GONE-1", result.FirstError.Description);
        Assert.Equal(0, await _context.VanSalesOrders.CountAsync());
    }

    [Fact]
    public async Task An_out_of_stock_item_is_still_accepted()
    {
        // Deliberate. Refusing would throw away demand the depot may restock before the van loads,
        // and the rep adjusts at delivery anyway.
        var accountId = await GivenCustomerAsync();
        await GivenVanSupplyingWarehouseAsync();
        await GivenStockAsync("FRM001", 0m);

        var result = await SubmitAsync(accountId, Key, ("FRM001", 100m));

        Assert.False(result.IsError);
    }

    [Fact]
    public async Task An_order_for_a_delivery_whose_cut_off_has_passed_is_refused()
    {
        // The case that matters for offline: an order built on Monday, queued, and arriving after
        // the van has been loaded. Accepting it would put stock on a van that has left.
        var accountId = await GivenCustomerAsync();
        await GivenVisitDaysAsync(DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
            DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday);

        var result = await SubmitAsync(
            accountId,
            Key,
            new[] { ("FRM001", 1m) },
            requestedVisitDate: DateTime.UtcNow.Date.AddDays(-1));

        Assert.True(result.IsError);
        Assert.Equal("VanSalesOrders.OrderingClosed", result.FirstError.Code);
    }

    [Fact]
    public async Task A_deactivated_sign_in_cannot_order()
    {
        var accountId = await GivenCustomerAsync();
        await _context.VanSalesCustomerAccounts.ExecuteUpdateAsync(
            s => s.SetProperty(a => a.IsActive, false));
        _context.ChangeTracker.Clear();

        var result = await SubmitAsync(accountId, Key, ("FRM001", 1m));

        Assert.True(result.IsError);
        Assert.Equal("VanSalesCustomerAuth.AccountInactive", result.FirstError.Code);
    }

    // ── The order records where it came from ────────────

    [Fact]
    public async Task The_shop_and_its_round_are_snapshotted_onto_the_order()
    {
        // Route customers are hard-deleted and reassigned. An order from March has to keep saying
        // which shop placed it and which van was to carry it.
        var accountId = await GivenCustomerAsync();
        await GivenVanOnRouteAsync();

        await SubmitAsync(accountId, Key, ("FRM001", 1m));

        var order = await _context.VanSalesOrders.AsNoTracking().SingleAsync();
        Assert.Equal("CUST-1", order.RouteCustomerCode);
        Assert.Equal("Shop CUST-1", order.RouteCustomerName);
        Assert.Equal("BP-1", order.AssignedBusinessPartnerCode);
        Assert.Equal("GUR", order.RouteCode);
    }

    [Fact]
    public async Task The_handsets_clock_is_recorded_but_the_server_decides_the_time()
    {
        var accountId = await GivenCustomerAsync();
        var deviceClock = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await SubmitAsync(accountId, Key, new[] { ("FRM001", 1m) }, submittedAtUtc: deviceClock);

        var order = await _context.VanSalesOrders.AsNoTracking().SingleAsync();
        Assert.Equal(deviceClock, order.SubmittedAtUtc);
        Assert.True(order.ReceivedAtUtc > deviceClock);
    }

    // ── Fixture ─────────────────────────────────────────

    private Task<ErrorOr.ErrorOr<VanSalesOrderResult>> SubmitAsync(
        int accountId,
        string clientRequestId,
        params (string ItemCode, decimal Quantity)[] lines)
        => SubmitAsync(accountId, clientRequestId, lines, null, null);

    private async Task<ErrorOr.ErrorOr<VanSalesOrderResult>> SubmitAsync(
        int accountId,
        string clientRequestId,
        IReadOnlyList<(string ItemCode, decimal Quantity)> lines,
        DateTime? requestedVisitDate = null,
        DateTime? submittedAtUtc = null)
    {
        var handler = new SubmitVanSalesCustomerOrderHandler(
            _context,
            CatalogueReader(),
            new FixedRules(),
            new NoOpAuditService(),
            NullLogger<SubmitVanSalesCustomerOrderHandler>.Instance);

        var result = await handler.Handle(
            new SubmitVanSalesCustomerOrderCommand(
                accountId,
                clientRequestId,
                lines.Select(l => new SubmitVanSalesCustomerOrderLine(l.ItemCode, l.Quantity)).ToList(),
                requestedVisitDate,
                null,
                submittedAtUtc,
                "Test Handset",
                "1.0.0",
                null,
                null),
            default);

        _context.ChangeTracker.Clear();
        return result;
    }

    private VanSalesCatalogueReader CatalogueReader() => new(
        _context,
        _prices,
        new FixedRules(),
        Options.Create(new TaxSettings { VatRate = 0.155m }),
        NullLogger<VanSalesCatalogueReader>.Instance);

    private async Task<int> GivenCustomerAsync()
    {
        _context.RouteCustomers.Add(new RouteCustomerEntity
        {
            Id = 1,
            AssignedBusinessPartnerCode = "BP-1",
            Code = "CUST-1",
            Name = "Shop CUST-1",
            IsActive = true
        });
        await _context.SaveChangesAsync();

        var account = new VanSalesCustomerAccountEntity
        {
            RouteCustomerId = 1,
            PhoneE164 = "+263771234567",
            IsActive = true
        };
        _context.VanSalesCustomerAccounts.Add(account);
        await _context.SaveChangesAsync();

        await GivenProductAsync("FRM001", "Feta 200g", 2.00m);
        await GivenProductAsync("FRM002", "Halloumi", 3.00m);

        _context.ChangeTracker.Clear();
        return account.Id;
    }

    private async Task GivenProductAsync(string itemCode, string itemName, decimal price)
    {
        var merchandiser = Guid.Parse("66666666-6666-6666-6666-666666666666");
        await GivenUserAsync(merchandiser, "merch-1", ApplicationRoles.Merchandiser, null, null);

        _context.MerchandiserProducts.Add(new MerchandiserProductEntity
        {
            MerchandiserUserId = merchandiser,
            ItemCode = itemCode,
            ItemName = itemName,
            UoM = "EA",
            Category = "Cheese",
            IsActive = true
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        _prices.Set(itemCode, price);
    }

    private Task GivenVanOnRouteAsync() => GivenVanAsync(routeCode: "GUR", supplyingWarehouse: null);

    private Task GivenVanSupplyingWarehouseAsync() => GivenVanAsync(routeCode: null, supplyingWarehouse: "KEFGRC");

    private async Task GivenVanAsync(string? routeCode, string? supplyingWarehouse)
    {
        int? routeId = null;

        if (routeCode is not null)
        {
            var route = new RouteEntity { Code = routeCode, Name = "Guruve", Territory = "UPC", IsActive = true };
            _context.Routes.Add(route);
            await _context.SaveChangesAsync();
            routeId = route.Id;
        }

        await GivenUserAsync(
            Guid.Parse("55555555-5555-5555-5555-555555555555"),
            "van-1",
            ApplicationRoles.Sales,
            routeId,
            supplyingWarehouse);
    }

    private async Task GivenUserAsync(
        Guid id,
        string username,
        string role,
        int? routeId,
        string? supplyingWarehouse)
    {
        if (await _context.Users.AnyAsync(u => u.Id == id))
        {
            return;
        }

        _context.Users.Add(new User
        {
            Id = id,
            Username = username,
            Email = $"{username}@example.test",
            PasswordHash = "x",
            Role = role,
            AssignedBusinessPartnerCode = role == ApplicationRoles.Sales ? "BP-1" : null,
            RouteId = routeId,
            SupplyingWarehouseCode = supplyingWarehouse,
            IsActive = true
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private async Task GivenVisitDaysAsync(params DayOfWeek[] days)
    {
        _context.RouteCustomerVisitDays.AddRange(days.Select(d => new RouteCustomerVisitDayEntity
        {
            RouteCustomerId = 1,
            DayOfWeek = d
        }));
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private async Task GivenStockAsync(string itemCode, decimal available)
    {
        var snapshot = new DailyStockSnapshotEntity
        {
            SnapshotDate = DateTime.UtcNow.Date,
            WarehouseCode = "KEFGRC"
        };
        _context.DailyStockSnapshots.Add(snapshot);
        await _context.SaveChangesAsync();

        _context.DailyStockSnapshotItems.Add(new DailyStockSnapshotItemEntity
        {
            SnapshotId = snapshot.Id,
            ItemCode = itemCode,
            WarehouseCode = "KEFGRC",
            OriginalQuantity = available,
            AvailableQuantity = available
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private sealed class FixedRules : IVanSalesOrderingPolicy
    {
        public Task<VanSalesOrderingRules> GetRulesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new VanSalesOrderingRules(8, 1, 10m));
    }

    private sealed class NoOpAuditService : IAuditService
    {
        public Task LogAsync(string action, string username, string userRole, string? entityType = null,
            string? entityId = null, string? details = null, string? endpoint = null,
            bool isSuccess = true, string? errorMessage = null) => Task.CompletedTask;

        public Task LogAsync(string action, string? entityType = null, string? entityId = null)
            => Task.CompletedTask;

        public Task LogAsync(string action, string? entityType, string? entityId, string? details,
            bool isSuccess, string? errorMessage = null) => Task.CompletedTask;
    }

    private sealed class StubPriceCatalog : ILocalPriceCatalogService
    {
        private readonly Dictionary<string, decimal> _prices = new(StringComparer.OrdinalIgnoreCase);

        public void Set(string itemCode, decimal price) => _prices[itemCode] = price;

        public Task<ItemPricesByListResponseDto> GetPricesByPriceListAsync(
            int priceListNum,
            IReadOnlyCollection<string>? itemCodes = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ItemPricesByListResponseDto
            {
                PriceListNum = priceListNum,
                Currency = "USD",
                TotalCount = _prices.Count,
                Prices = _prices
                    .Select(kvp => new ItemPriceByListDto { ItemCode = kvp.Key, Price = kvp.Value })
                    .ToList()
            });

        public Task<PriceListsResponseDto> GetPriceListsAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ItemPricesResponseDto> GetAllPricesAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ItemPricesGroupedResponseDto> GetGroupedPricesAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ItemPriceGroupedDto?> GetGroupedPriceByItemCodeAsync(string itemCode, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ItemPricesResponseDto> GetPricesByCurrencyAsync(string currency, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ItemPriceByListDto?> GetItemPriceFromListAsync(int priceListNum, string itemCode, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<Dictionary<string, decimal>> GetActiveSpecialPricesAsync(string cardCode, IReadOnlyCollection<string>? itemCodes = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<LocalBusinessPartnerPricingResult?> GetBusinessPartnerPricingAsync(string cardCode, IReadOnlyCollection<string>? itemCodes = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// <see cref="DailyStockSnapshotItemEntity.Version"/> and
    /// <see cref="VanSalesOrderEntity.Version"/> are <c>[Timestamp]</c>, which Npgsql maps to the
    /// store-generated <c>xmin</c>. SQLite has no equivalent, so EF omits the column from the
    /// INSERT and the NOT NULL constraint fails. Made ordinary properties here so the fixture can
    /// supply them; nothing under test reads either.
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

            modelBuilder.Entity<VanSalesOrderEntity>()
                .Property(order => order.Version)
                .ValueGeneratedNever()
                .IsConcurrencyToken(false);
        }
    }
}
