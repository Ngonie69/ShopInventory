using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.Features.VanSalesOrders;
using ShopInventory.Features.VanSalesOrders.Commands.CancelVanSalesCustomerOrder;
using ShopInventory.Features.VanSalesOrders.Queries.GetVanSalesCustomerOrderByClientRequestId;
using ShopInventory.Features.VanSalesOrders.Queries.GetVanSalesCustomerOrderById;
using ShopInventory.Features.VanSalesOrders.Queries.GetVanSalesCustomerOrders;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Who may read and withdraw an order.
///
/// Every one of these endpoints is reachable by any signed-in shop, and the id in the URL is
/// whatever the caller types. Without the account being part of the query, a shopkeeper could page
/// through the id range and read a competitor's trading — what they buy, how much, how often — which
/// is exactly the information a rival most wants and a supplier is least entitled to leak.
///
/// Another shop's order reports as not found rather than forbidden. "Forbidden" confirms the order
/// exists, and the pair of answers is enough to count a competitor's orders without reading one.
///
/// Cancellation adds a second question: the cut-off. Past it the stock has been picked for this
/// shop, and a cancellation that silently succeeded would send a van out with goods the customer
/// believes they have cancelled.
/// </summary>
public sealed class VanSalesOrderOwnershipTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    private int _mine;
    private int _theirs;

    public VanSalesOrderOwnershipTests()
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

    // ── Reading ─────────────────────────────────────────

    [Fact]
    public async Task A_customer_can_read_their_own_order()
    {
        await GivenTwoShopsAsync();
        var orderId = await GivenOrderAsync(_mine, "key-mine");

        var result = await ByIdAsync(_mine, orderId);

        Assert.False(result.IsError);
        Assert.Equal(orderId, result.Value.Id);
    }

    [Fact]
    public async Task Another_shops_order_reads_as_not_found()
    {
        await GivenTwoShopsAsync();
        var theirOrder = await GivenOrderAsync(_theirs, "key-theirs");

        var result = await ByIdAsync(_mine, theirOrder);

        Assert.True(result.IsError);
        Assert.Equal("VanSalesOrders.NotFound", result.FirstError.Code);
    }

    [Fact]
    public async Task An_order_that_never_existed_reads_the_same_way()
    {
        // The two answers have to be identical, or the difference between them counts a
        // competitor's orders.
        await GivenTwoShopsAsync();
        var theirOrder = await GivenOrderAsync(_theirs, "key-theirs");

        var theirs = await ByIdAsync(_mine, theirOrder);
        var nothing = await ByIdAsync(_mine, 99999);

        Assert.Equal(nothing.FirstError.Code, theirs.FirstError.Code);
        Assert.Equal(nothing.FirstError.Description, theirs.FirstError.Description);
    }

    [Fact]
    public async Task The_history_holds_only_the_callers_own_orders()
    {
        await GivenTwoShopsAsync();
        await GivenOrderAsync(_mine, "key-mine-1");
        await GivenOrderAsync(_mine, "key-mine-2");
        await GivenOrderAsync(_theirs, "key-theirs");

        var result = await ListAsync(_mine);

        Assert.Equal(2, result.Value.TotalCount);
        Assert.All(result.Value.Orders, o => Assert.Equal("CUST-1", o.CustomerCode));
    }

    [Fact]
    public async Task Another_shops_idempotency_key_resolves_to_nothing()
    {
        // A client request id is a GUID and not realistically guessable, but the lookup is scoped
        // anyway — it costs nothing and this is an endpoint an authenticated customer could fish in.
        await GivenTwoShopsAsync();
        await GivenOrderAsync(_theirs, "key-theirs");

        var result = await ByClientRequestAsync(_mine, "key-theirs");

        Assert.True(result.IsError);
        Assert.Equal("VanSalesOrders.NotFound", result.FirstError.Code);
    }

    [Fact]
    public async Task A_key_that_produced_nothing_reports_not_found()
    {
        // This is what tells a handset it is safe to send again, so the negative answer matters as
        // much as the positive one.
        await GivenTwoShopsAsync();

        var result = await ByClientRequestAsync(_mine, "never-sent");

        Assert.True(result.IsError);
        Assert.Equal("VanSalesOrders.NotFound", result.FirstError.Code);
    }

    // ── Cancelling ──────────────────────────────────────

    [Fact]
    public async Task A_customer_can_cancel_their_own_open_order()
    {
        await GivenTwoShopsAsync();
        var orderId = await GivenOrderAsync(_mine, "key-mine");

        var result = await CancelAsync(_mine, orderId);

        Assert.False(result.IsError);
        Assert.Equal(VanSalesOrderStatus.Cancelled, result.Value.Status);
        Assert.NotNull(result.Value.CancelledAtUtc);
    }

    [Fact]
    public async Task A_customer_cannot_cancel_another_shops_order()
    {
        await GivenTwoShopsAsync();
        var theirOrder = await GivenOrderAsync(_theirs, "key-theirs");

        var result = await CancelAsync(_mine, theirOrder);

        Assert.True(result.IsError);
        Assert.Equal("VanSalesOrders.NotFound", result.FirstError.Code);

        var untouched = await _context.VanSalesOrders.AsNoTracking().SingleAsync(o => o.Id == theirOrder);
        Assert.Equal(VanSalesOrderStatus.Accepted, untouched.Status);
    }

    [Fact]
    public async Task Cancelling_twice_is_reported_rather_than_ignored()
    {
        // A customer tapping cancel on a stale screen deserves to know why nothing changed.
        await GivenTwoShopsAsync();
        var orderId = await GivenOrderAsync(_mine, "key-mine");

        await CancelAsync(_mine, orderId);
        var again = await CancelAsync(_mine, orderId);

        Assert.True(again.IsError);
        Assert.Equal("VanSalesOrders.AlreadyCancelled", again.FirstError.Code);
    }

    [Fact]
    public async Task A_delivered_order_cannot_be_cancelled()
    {
        await GivenTwoShopsAsync();
        var orderId = await GivenOrderAsync(_mine, "key-mine");
        await _context.VanSalesOrders
            .Where(o => o.Id == orderId)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, VanSalesOrderStatus.Fulfilled));
        _context.ChangeTracker.Clear();

        var result = await CancelAsync(_mine, orderId);

        Assert.True(result.IsError);
        Assert.Equal("VanSalesOrders.CannotCancel", result.FirstError.Code);
    }

    [Fact]
    public async Task An_order_past_its_cut_off_cannot_be_cancelled()
    {
        // The stock has been picked for this shop. Letting the cancellation appear to succeed would
        // send the van out with goods the customer believes they have cancelled.
        await GivenTwoShopsAsync();
        var orderId = await GivenOrderAsync(_mine, "key-mine", visitDate: DateTime.UtcNow.Date);

        var result = await CancelAsync(_mine, orderId);

        Assert.True(result.IsError);
        Assert.Equal("VanSalesOrders.CancellationWindowClosed", result.FirstError.Code);
    }

    [Fact]
    public async Task An_order_with_no_delivery_date_can_still_be_cancelled()
    {
        // A shop with no calling days has no cut-off to be past.
        await GivenTwoShopsAsync();
        var orderId = await GivenOrderAsync(_mine, "key-mine", visitDate: null);

        var result = await CancelAsync(_mine, orderId);

        Assert.False(result.IsError);
    }

    // ── Fixture ─────────────────────────────────────────

    private async Task<ErrorOr.ErrorOr<VanSalesOrderResult>> ByIdAsync(int accountId, int orderId)
    {
        var handler = new GetVanSalesCustomerOrderByIdHandler(_context);
        var result = await handler.Handle(new GetVanSalesCustomerOrderByIdQuery(accountId, orderId), default);
        _context.ChangeTracker.Clear();
        return result;
    }

    private async Task<ErrorOr.ErrorOr<VanSalesOrderResult>> ByClientRequestAsync(int accountId, string key)
    {
        var handler = new GetVanSalesCustomerOrderByClientRequestIdHandler(_context);
        var result = await handler.Handle(
            new GetVanSalesCustomerOrderByClientRequestIdQuery(accountId, key), default);
        _context.ChangeTracker.Clear();
        return result;
    }

    private async Task<ErrorOr.ErrorOr<VanSalesOrderListResult>> ListAsync(int accountId)
    {
        var handler = new GetVanSalesCustomerOrdersHandler(_context);
        var result = await handler.Handle(new GetVanSalesCustomerOrdersQuery(accountId, 1, 20), default);
        _context.ChangeTracker.Clear();
        return result;
    }

    private async Task<ErrorOr.ErrorOr<VanSalesOrderResult>> CancelAsync(int accountId, int orderId)
    {
        var handler = new CancelVanSalesCustomerOrderHandler(
            _context,
            new FixedRules(),
            new NoOpAuditService(),
            new SilentNotifier(),
            NullLogger<CancelVanSalesCustomerOrderHandler>.Instance);

        var result = await handler.Handle(
            new CancelVanSalesCustomerOrderCommand(accountId, orderId, "changed my mind"), default);
        _context.ChangeTracker.Clear();
        return result;
    }

    private async Task GivenTwoShopsAsync()
    {
        _mine = await GivenShopAsync(1, "CUST-1", "+263771111111");
        _theirs = await GivenShopAsync(2, "CUST-2", "+263772222222");
    }

    private async Task<int> GivenShopAsync(int routeCustomerId, string code, string phone)
    {
        _context.RouteCustomers.Add(new RouteCustomerEntity
        {
            Id = routeCustomerId,
            AssignedBusinessPartnerCode = "BP-1",
            Code = code,
            Name = $"Shop {code}",
            IsActive = true
        });
        await _context.SaveChangesAsync();

        var account = new VanSalesCustomerAccountEntity
        {
            RouteCustomerId = routeCustomerId,
            PhoneE164 = phone,
            IsActive = true
        };
        _context.VanSalesCustomerAccounts.Add(account);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        return account.Id;
    }

    /// <summary>
    /// An accepted order. The default delivery date is a fortnight out so the cut-off has not
    /// passed — the tests that care about the cut-off set their own.
    /// </summary>
    private async Task<int> GivenOrderAsync(
        int accountId,
        string clientRequestId,
        DateTime? visitDate = null)
    {
        var account = await _context.VanSalesCustomerAccounts
            .AsNoTracking()
            .Include(a => a.RouteCustomer)
            .SingleAsync(a => a.Id == accountId);

        var order = new VanSalesOrderEntity
        {
            OrderNumber = $"VSO-TEST-{clientRequestId}",
            VanSalesCustomerAccountId = accountId,
            RouteCustomerId = account.RouteCustomerId,
            RouteCustomerCode = account.RouteCustomer!.Code,
            RouteCustomerName = account.RouteCustomer.Name,
            ClientRequestId = clientRequestId,
            Status = VanSalesOrderStatus.Accepted,
            RequestedVisitDate = visitDate ?? DateTime.UtcNow.Date.AddDays(14),
            Currency = "USD",
            SubTotal = 10m,
            TaxAmount = 1.55m,
            DocTotal = 11.55m,
            ReceivedAtUtc = DateTime.UtcNow
        };

        order.Lines.Add(new VanSalesOrderLineEntity
        {
            LineNumber = 1,
            ItemCode = "FRM001",
            ItemDescription = "Feta 200g",
            QuantityOrdered = 5m,
            UnitPrice = 2m,
            TaxPercent = 15.5m,
            LineTotal = 10m
        });

        _context.VanSalesOrders.Add(order);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        return order.Id;
    }

    /// <summary>Pushing is covered by VanSalesOrderDeliveryTests; here it just must not throw.</summary>
    private sealed class SilentNotifier : IVanSalesCustomerNotifier
    {
        public Task NotifyOrderStatusAsync(VanSalesOrderEntity order, CancellationToken cancellationToken)
            => Task.CompletedTask;
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

    /// <summary>
    /// <c>[Timestamp]</c> properties map to PostgreSQL's <c>xmin</c>, which SQLite has no
    /// equivalent for. Made ordinary here so the fixture can insert; nothing under test reads them.
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
