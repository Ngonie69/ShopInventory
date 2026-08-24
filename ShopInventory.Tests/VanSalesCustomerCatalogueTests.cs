using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.VanSalesOrders;
using ShopInventory.Features.VanSalesOrders.Queries.GetVanSalesCustomerCatalogue;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The priced catalogue a shopkeeper browses.
///
/// Two things here are worth more than the item list itself. The first is that a customer is shown
/// a stock <em>band</em> and never a quantity: what a depot holds is commercially sensitive, and a
/// figure taken the afternoon before the van is loaded is not a promise anybody can keep. The
/// second is that an item with no price on the configured list is dropped rather than shown at
/// zero — an item a customer can add to a basket for free is an order somebody unpicks by hand.
///
/// Everything also has to degrade rather than fail. A depot with no stock snapshot, a van with no
/// supplying warehouse recorded, an empty price list: none of them may stop a shop opening the app.
/// </summary>
public sealed class VanSalesCustomerCatalogueTests : IDisposable
{
    private const string Depot = "KEFGRC";
    private static readonly Guid VanUser = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly StubPriceCatalog _prices = new();

    public VanSalesCustomerCatalogueTests()
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

    // ── Pricing ─────────────────────────────────────────

    [Fact]
    public async Task Items_are_priced_from_the_configured_price_list()
    {
        var accountId = await GivenCustomerAsync();
        await GivenProductAsync("FRM001", "Feta 200g");
        _prices.Set("FRM001", 2.00m);

        var result = await CatalogueAsync(accountId);

        Assert.False(result.IsError);
        var item = Assert.Single(result.Value.Items);
        Assert.Equal("FRM001", item.ItemCode);
        Assert.Equal(2.00m, item.UnitPrice);
        Assert.Equal(1, _prices.LastPriceListRequested);
    }

    [Fact]
    public async Task Both_a_net_and_a_gross_price_are_sent()
    {
        // The shopkeeper thinks in what they will pay; the order maths is done on the net. Having
        // the handset derive one from the other is how the two ends come to disagree by a cent.
        var accountId = await GivenCustomerAsync();
        await GivenProductAsync("FRM001", "Feta 200g");
        _prices.Set("FRM001", 2.00m);

        var item = (await CatalogueAsync(accountId)).Value.Items.Single();

        Assert.Equal(15.5m, item.TaxPercent);
        Assert.Equal(2.31m, item.UnitPriceIncludingTax);
    }

    [Fact]
    public async Task An_item_with_no_price_on_the_list_is_left_out()
    {
        var accountId = await GivenCustomerAsync();
        await GivenProductAsync("FRM001", "Feta 200g");
        await GivenProductAsync("FRM009", "Unpriced thing");
        _prices.Set("FRM001", 2.00m);

        var result = await CatalogueAsync(accountId);

        Assert.Equal(["FRM001"], result.Value.Items.Select(i => i.ItemCode));
    }

    [Fact]
    public async Task An_item_priced_at_zero_is_left_out()
    {
        // Free stock is a data error, not an offer.
        var accountId = await GivenCustomerAsync();
        await GivenProductAsync("FRM001", "Feta 200g");
        _prices.Set("FRM001", 0m);

        Assert.Empty((await CatalogueAsync(accountId)).Value.Items);
    }

    [Fact]
    public async Task An_item_assigned_to_several_merchandisers_appears_once()
    {
        // MerchandiserProducts holds a row per merchandiser, so the same item is in there many
        // times over. A duplicated catalogue line is a shopkeeper adding the same thing twice.
        var accountId = await GivenCustomerAsync();
        await GivenProductAsync("FRM001", "Feta 200g");
        await GivenProductAsync("FRM001", "Feta 200g", merchandiser: Guid.NewGuid());
        _prices.Set("FRM001", 2.00m);

        Assert.Single((await CatalogueAsync(accountId)).Value.Items);
    }

    [Fact]
    public async Task An_inactive_product_is_not_offered()
    {
        var accountId = await GivenCustomerAsync();
        await GivenProductAsync("FRM001", "Feta 200g", isActive: false);
        _prices.Set("FRM001", 2.00m);

        Assert.Empty((await CatalogueAsync(accountId)).Value.Items);
    }

    // ── Stock bands ─────────────────────────────────────

    [Fact]
    public async Task Plenty_in_the_depot_reads_as_in_stock()
    {
        var accountId = await GivenCustomerAsync();
        await GivenVanSupplyingWarehouseAsync();
        await GivenProductAsync("FRM001", "Feta 200g");
        _prices.Set("FRM001", 2.00m);
        await GivenStockAsync("FRM001", 500m);

        var item = (await CatalogueAsync(accountId)).Value.Items.Single();

        Assert.Equal(VanSalesStockBand.InStock, item.Availability);
    }

    [Fact]
    public async Task At_or_below_the_threshold_reads_as_low()
    {
        var accountId = await GivenCustomerAsync();
        await GivenVanSupplyingWarehouseAsync();
        await GivenProductAsync("FRM001", "Feta 200g");
        _prices.Set("FRM001", 2.00m);
        await GivenStockAsync("FRM001", 10m);

        var item = (await CatalogueAsync(accountId)).Value.Items.Single();

        Assert.Equal(VanSalesStockBand.Low, item.Availability);
    }

    [Fact]
    public async Task Nothing_counted_reads_as_out_of_stock()
    {
        var accountId = await GivenCustomerAsync();
        await GivenVanSupplyingWarehouseAsync();
        await GivenProductAsync("FRM001", "Feta 200g");
        await GivenProductAsync("FRM002", "Halloumi");
        _prices.Set("FRM001", 2.00m);
        _prices.Set("FRM002", 3.00m);
        await GivenStockAsync("FRM001", 0m);
        // FRM002 has no row in the snapshot at all, which also means none was counted.

        var items = (await CatalogueAsync(accountId)).Value.Items.ToDictionary(i => i.ItemCode);

        Assert.Equal(VanSalesStockBand.OutOfStock, items["FRM001"].Availability);
        Assert.Equal(VanSalesStockBand.OutOfStock, items["FRM002"].Availability);
    }

    [Fact]
    public async Task Batches_of_the_same_item_are_summed()
    {
        // The snapshot holds a row per batch; a shopkeeper ordering a case does not care which
        // batch fills it. Three lots of four is twelve, which is above the threshold of ten — read
        // batch by batch, every one of them would look low.
        var accountId = await GivenCustomerAsync();
        await GivenVanSupplyingWarehouseAsync();
        await GivenProductAsync("FRM001", "Feta 200g");
        _prices.Set("FRM001", 2.00m);
        await GivenStockAsync("FRM001", 4m, batch: "B1");
        await GivenStockAsync("FRM001", 4m, batch: "B2");
        await GivenStockAsync("FRM001", 4m, batch: "B3");

        var item = (await CatalogueAsync(accountId)).Value.Items.Single();

        Assert.Equal(VanSalesStockBand.InStock, item.Availability);
    }

    [Fact]
    public async Task A_van_with_no_supplying_warehouse_gives_unknown_rather_than_a_guess()
    {
        var accountId = await GivenCustomerAsync();
        await GivenProductAsync("FRM001", "Feta 200g");
        _prices.Set("FRM001", 2.00m);

        var result = await CatalogueAsync(accountId);

        Assert.Null(result.Value.StockWarehouseCode);
        Assert.Equal(VanSalesStockBand.Unknown, result.Value.Items.Single().Availability);
    }

    [Fact]
    public async Task A_depot_with_no_snapshot_gives_unknown_rather_than_out_of_stock()
    {
        // Saying "out of stock" for a warehouse nobody has counted would stop customers ordering
        // things that are sitting on the floor.
        var accountId = await GivenCustomerAsync();
        await GivenVanSupplyingWarehouseAsync();
        await GivenProductAsync("FRM001", "Feta 200g");
        _prices.Set("FRM001", 2.00m);

        var item = (await CatalogueAsync(accountId)).Value.Items.Single();

        Assert.Equal(VanSalesStockBand.Unknown, item.Availability);
    }

    [Fact]
    public async Task The_most_recent_snapshot_wins()
    {
        var accountId = await GivenCustomerAsync();
        await GivenVanSupplyingWarehouseAsync();
        await GivenProductAsync("FRM001", "Feta 200g");
        _prices.Set("FRM001", 2.00m);
        await GivenStockAsync("FRM001", 500m, snapshotDate: DateTime.UtcNow.Date.AddDays(-1));
        await GivenStockAsync("FRM001", 0m, snapshotDate: DateTime.UtcNow.Date);

        var item = (await CatalogueAsync(accountId)).Value.Items.Single();

        Assert.Equal(VanSalesStockBand.OutOfStock, item.Availability);
    }

    [Fact]
    public async Task No_quantity_ever_reaches_the_customer()
    {
        // The band is the whole point. If a quantity ever appears on this contract, a UI will show
        // it, and what the depot holds is not a customer's business.
        var accountId = await GivenCustomerAsync();
        await GivenVanSupplyingWarehouseAsync();
        await GivenProductAsync("FRM001", "Feta 200g");
        _prices.Set("FRM001", 2.00m);
        await GivenStockAsync("FRM001", 437m);

        var item = (await CatalogueAsync(accountId)).Value.Items.Single();

        var properties = item.GetType().GetProperties().Select(p => p.Name).ToArray();
        Assert.DoesNotContain("AvailableQuantity", properties);
        Assert.DoesNotContain("Quantity", properties);
        Assert.DoesNotContain("StockOnHand", properties);
    }

    // ── The ETag contract ───────────────────────────────

    [Fact]
    public async Task An_unchanged_catalogue_keeps_the_same_tag()
    {
        var accountId = await GivenCustomerAsync();
        await GivenProductAsync("FRM001", "Feta 200g");
        _prices.Set("FRM001", 2.00m);

        var first = await CatalogueAsync(accountId);
        var second = await CatalogueAsync(accountId);

        Assert.Equal(first.Value.ETag, second.Value.ETag);
    }

    [Fact]
    public async Task A_price_change_changes_the_tag()
    {
        var accountId = await GivenCustomerAsync();
        await GivenProductAsync("FRM001", "Feta 200g");
        _prices.Set("FRM001", 2.00m);
        var before = (await CatalogueAsync(accountId)).Value.ETag;

        _prices.Set("FRM001", 2.50m);
        var after = (await CatalogueAsync(accountId)).Value.ETag;

        Assert.NotEqual(before, after);
    }

    [Fact]
    public async Task A_stock_band_change_changes_the_tag()
    {
        // The one a naive tag over the item list would miss — and the one a handset most needs to
        // be told about, because it decides whether the shopkeeper orders at all.
        var accountId = await GivenCustomerAsync();
        await GivenVanSupplyingWarehouseAsync();
        await GivenProductAsync("FRM001", "Feta 200g");
        _prices.Set("FRM001", 2.00m);
        await GivenStockAsync("FRM001", 500m, snapshotDate: DateTime.UtcNow.Date.AddDays(-1));
        var before = (await CatalogueAsync(accountId)).Value.ETag;

        await GivenStockAsync("FRM001", 0m, snapshotDate: DateTime.UtcNow.Date);
        var after = (await CatalogueAsync(accountId)).Value.ETag;

        Assert.NotEqual(before, after);
    }

    // ── Access ──────────────────────────────────────────

    [Fact]
    public async Task A_deactivated_sign_in_gets_no_catalogue()
    {
        var accountId = await GivenCustomerAsync();
        await _context.VanSalesCustomerAccounts.ExecuteUpdateAsync(
            s => s.SetProperty(a => a.IsActive, false));
        _context.ChangeTracker.Clear();

        var result = await CatalogueAsync(accountId);

        Assert.True(result.IsError);
        Assert.Equal("VanSalesCustomerAuth.AccountInactive", result.FirstError.Code);
    }

    // ── Fixture ─────────────────────────────────────────

    private async Task<ErrorOr.ErrorOr<VanSalesCatalogueResult>> CatalogueAsync(int accountId)
    {
        var handler = new GetVanSalesCustomerCatalogueHandler(
            _context,
            CatalogueReader(),
            new FixedRules(),
            NullLogger<GetVanSalesCustomerCatalogueHandler>.Instance);

        var result = await handler.Handle(new GetVanSalesCustomerCatalogueQuery(accountId), default);
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
        _context.ChangeTracker.Clear();

        return account.Id;
    }

    private async Task GivenVanSupplyingWarehouseAsync()
    {
        _context.Users.Add(new User
        {
            Id = VanUser,
            Username = "van-1",
            Email = "van-1@example.test",
            PasswordHash = "x",
            Role = ApplicationRoles.Sales,
            AssignedBusinessPartnerCode = "BP-1",
            SupplyingWarehouseCode = Depot,
            IsActive = true
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private async Task GivenUserAsync(Guid id, string username, string role)
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
            IsActive = true
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private async Task GivenProductAsync(
        string itemCode,
        string itemName,
        bool isActive = true,
        Guid? merchandiser = null)
    {
        // MerchandiserProducts.MerchandiserUserId is a foreign key, so the merchandiser has to
        // exist before a product can be assigned to them.
        var merchandiserId = merchandiser ?? Guid.Parse("66666666-6666-6666-6666-666666666666");
        await GivenUserAsync(merchandiserId, $"merch-{merchandiserId:N}"[..12], ApplicationRoles.Merchandiser);

        _context.MerchandiserProducts.Add(new MerchandiserProductEntity
        {
            MerchandiserUserId = merchandiserId,
            ItemCode = itemCode,
            ItemName = itemName,
            BarCode = $"BC-{itemCode}",
            UoM = "EA",
            Category = "Cheese",
            IsActive = isActive
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private async Task GivenStockAsync(
        string itemCode,
        decimal available,
        string? batch = null,
        DateTime? snapshotDate = null)
    {
        var date = snapshotDate ?? DateTime.UtcNow.Date;

        var snapshot = await _context.DailyStockSnapshots
            .FirstOrDefaultAsync(s => s.SnapshotDate == date && s.WarehouseCode == Depot);

        if (snapshot is null)
        {
            snapshot = new DailyStockSnapshotEntity { SnapshotDate = date, WarehouseCode = Depot };
            _context.DailyStockSnapshots.Add(snapshot);
            await _context.SaveChangesAsync();
        }

        _context.DailyStockSnapshotItems.Add(new DailyStockSnapshotItemEntity
        {
            SnapshotId = snapshot.Id,
            ItemCode = itemCode,
            WarehouseCode = Depot,
            BatchNumber = batch,
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

    /// <summary>
    /// Stands in for the synced SAP price catalogue. Only the price-list read is used by the
    /// catalogue; the rest throws so a handler that quietly started calling something else — a live
    /// SAP path, or a per-customer special price these customers do not have — fails loudly here.
    /// </summary>
    private sealed class StubPriceCatalog : ILocalPriceCatalogService
    {
        private readonly Dictionary<string, decimal> _prices = new(StringComparer.OrdinalIgnoreCase);

        public int? LastPriceListRequested { get; private set; }

        public void Set(string itemCode, decimal price) => _prices[itemCode] = price;

        public Task<ItemPricesByListResponseDto> GetPricesByPriceListAsync(
            int priceListNum,
            IReadOnlyCollection<string>? itemCodes = null,
            CancellationToken cancellationToken = default)
        {
            LastPriceListRequested = priceListNum;

            return Task.FromResult(new ItemPricesByListResponseDto
            {
                PriceListNum = priceListNum,
                PriceListName = "Test List",
                Currency = "USD",
                TotalCount = _prices.Count,
                Prices = _prices
                    .Select(kvp => new ItemPriceByListDto { ItemCode = kvp.Key, Price = kvp.Value })
                    .ToList()
            });
        }

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
    /// <see cref="DailyStockSnapshotItemEntity.Version"/> is <c>[Timestamp]</c>, mapped by Npgsql to
    /// the store-generated <c>xmin</c>, which SQLite has no equivalent for. These tests do insert
    /// snapshot items, so this override is load-bearing here rather than defensive.
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
