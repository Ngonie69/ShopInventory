using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.Features.VanSalesOrders;
using ShopInventory.Features.VanSalesOrders.Commands.RecordVanSalesOrderDelivery;
using ShopInventory.Features.VanSalesOrders.Queries.GetVanSalesOrdersForRoute;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Recording what the van actually delivered, and building the list it loads from.
///
/// This is the half of the feature that closes the loop WhatsApp never had. An order said what the
/// shop wanted; the delivery record says what arrived, and the gap between them is a figure both
/// sides can read instead of two people remembering differently at the door.
///
/// That only works if the status is derived from the quantities rather than asserted. A record that
/// says "Fulfilled" over short lines is worse than no record: it is the supplier's word, written
/// down, contradicting its own detail.
/// </summary>
public sealed class VanSalesOrderDeliveryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public VanSalesOrderDeliveryTests()
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

    // ── Deriving the status ─────────────────────────────

    [Fact]
    public async Task Everything_delivered_closes_the_order_as_fulfilled()
    {
        var orderId = await GivenOrderAsync(("FRM001", 6m), ("FRM002", 4m));

        var result = await RecordAsync(orderId, (1, 6m), (2, 4m));

        Assert.False(result.IsError);
        Assert.Equal(VanSalesOrderStatus.Fulfilled, result.Value.Status);
        Assert.NotNull(result.Value.DeliveredAtUtc);
    }

    [Fact]
    public async Task A_short_line_makes_the_whole_order_part_delivered()
    {
        // The status a shopkeeper will query, so it must not be buried under a neutral word.
        var orderId = await GivenOrderAsync(("FRM001", 6m), ("FRM002", 4m));

        var result = await RecordAsync(orderId, (1, 4m), (2, 4m));

        Assert.Equal(VanSalesOrderStatus.PartiallyFulfilled, result.Value.Status);
    }

    [Fact]
    public async Task The_shortfall_is_readable_line_by_line()
    {
        var orderId = await GivenOrderAsync(("FRM001", 6m));

        var result = await RecordAsync(orderId, (1, 4m));

        var line = result.Value.Lines.Single();
        Assert.Equal(6m, line.QuantityOrdered);
        Assert.Equal(4m, line.QuantityFulfilled);
    }

    [Fact]
    public async Task Nothing_delivered_is_not_recorded_as_fulfilled()
    {
        // The van came and the shop got nothing. Calling that "Fulfilled with zeroes" would hide
        // the one outcome worth counting.
        var orderId = await GivenOrderAsync(("FRM001", 6m));

        var result = await RecordAsync(orderId, (1, 0m));

        Assert.Equal(VanSalesOrderStatus.Expired, result.Value.Status);
    }

    [Fact]
    public async Task A_line_not_mentioned_is_left_alone_rather_than_zeroed()
    {
        // A rep recording the one line they were short on must not thereby declare that nothing
        // else arrived — and a submission cut off halfway must not read as a complete one.
        var orderId = await GivenOrderAsync(("FRM001", 6m), ("FRM002", 4m));
        await RecordAsync(orderId, (1, 6m), (2, 4m));

        var result = await RecordAsync(orderId, (1, 5m));

        var lines = result.Value.Lines.ToDictionary(l => l.LineNumber);
        Assert.Equal(5m, lines[1].QuantityFulfilled);
        Assert.Equal(4m, lines[2].QuantityFulfilled);
    }

    // ── What is refused ─────────────────────────────────

    [Fact]
    public async Task Delivering_more_than_was_ordered_is_refused()
    {
        // Extra goods handed over at the door are a sale the rep makes, and belong on an invoice.
        // Inflating the order would put figures on the customer's screen they never agreed to.
        var orderId = await GivenOrderAsync(("FRM001", 6m));

        var result = await RecordAsync(orderId, (1, 10m));

        Assert.True(result.IsError);
        Assert.Equal("VanSalesOrders.OverDelivered", result.FirstError.Code);
        Assert.Contains("FRM001", result.FirstError.Description);
    }

    [Fact]
    public async Task A_line_that_does_not_exist_is_refused()
    {
        var orderId = await GivenOrderAsync(("FRM001", 6m));

        var result = await RecordAsync(orderId, (99, 1m));

        Assert.True(result.IsError);
        Assert.Equal("VanSalesOrders.UnknownLines", result.FirstError.Code);
    }

    [Fact]
    public async Task A_cancelled_order_cannot_be_delivered_against()
    {
        var orderId = await GivenOrderAsync(("FRM001", 6m));
        await _context.VanSalesOrders
            .Where(o => o.Id == orderId)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, VanSalesOrderStatus.Cancelled));
        _context.ChangeTracker.Clear();

        var result = await RecordAsync(orderId, (1, 6m));

        Assert.True(result.IsError);
        Assert.Equal("VanSalesOrders.AlreadyCancelled", result.FirstError.Code);
    }

    [Fact]
    public async Task The_customer_is_told_when_a_delivery_is_recorded()
    {
        // The push is the point of recording it promptly: a shopkeeper who is told their order came
        // up short can act on it while the van is still in the area.
        var notifier = new RecordingNotifier();
        var orderId = await GivenOrderAsync(("FRM001", 6m));

        await RecordAsync(orderId, notifier, (1, 4m));

        var notified = Assert.Single(notifier.Notified);
        Assert.Equal(VanSalesOrderStatus.PartiallyFulfilled, notified);
    }

    // ── The load list ───────────────────────────────────

    [Fact]
    public async Task The_load_list_totals_an_item_across_every_order()
    {
        // The figure the depot loads to. Adding it up by hand from a dozen orders at five in the
        // afternoon is exactly the arithmetic that goes wrong.
        var visitDate = DateTime.UtcNow.Date.AddDays(1);
        await GivenOrderAsync(visitDate, "CUST-1", ("FRM001", 6m), ("FRM002", 2m));
        await GivenOrderAsync(visitDate, "CUST-2", ("FRM001", 4m));

        var result = await LoadListAsync(visitDate);

        Assert.False(result.IsError);
        Assert.Equal(2, result.Value.OrderCount);

        var frm001 = result.Value.LoadLines.Single(l => l.ItemCode == "FRM001");
        Assert.Equal(10m, frm001.QuantityOrdered);
        Assert.Equal(2, frm001.OrderCount);
    }

    [Fact]
    public async Task A_cancelled_order_is_not_on_the_load_list()
    {
        // Stock loaded for nobody. The list has to be trustworthy at face value.
        var visitDate = DateTime.UtcNow.Date.AddDays(1);
        var keep = await GivenOrderAsync(visitDate, "CUST-1", ("FRM001", 6m));
        var drop = await GivenOrderAsync(visitDate, "CUST-2", ("FRM001", 4m));

        await _context.VanSalesOrders
            .Where(o => o.Id == drop)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, VanSalesOrderStatus.Cancelled));
        _context.ChangeTracker.Clear();

        var result = await LoadListAsync(visitDate);

        Assert.Equal(1, result.Value.OrderCount);
        Assert.Equal(6m, result.Value.LoadLines.Single().QuantityOrdered);
        Assert.Equal(keep, result.Value.Orders.Single().Id);
    }

    [Fact]
    public async Task The_load_list_is_scoped_to_the_day_asked_for()
    {
        var tuesday = DateTime.UtcNow.Date.AddDays(1);
        var friday = DateTime.UtcNow.Date.AddDays(4);
        await GivenOrderAsync(tuesday, "CUST-1", ("FRM001", 6m));
        await GivenOrderAsync(friday, "CUST-2", ("FRM001", 4m));

        var result = await LoadListAsync(tuesday);

        Assert.Equal(1, result.Value.OrderCount);
        Assert.Equal(6m, result.Value.LoadLines.Single().QuantityOrdered);
    }

    // ── Fixture ─────────────────────────────────────────

    private Task<ErrorOr.ErrorOr<VanSalesOrderResult>> RecordAsync(
        int orderId,
        params (int LineNumber, decimal Quantity)[] lines)
        => RecordAsync(orderId, new RecordingNotifier(), lines);

    private async Task<ErrorOr.ErrorOr<VanSalesOrderResult>> RecordAsync(
        int orderId,
        RecordingNotifier notifier,
        params (int LineNumber, decimal Quantity)[] lines)
    {
        var handler = new RecordVanSalesOrderDeliveryHandler(
            _context,
            new NoOpAuditService(),
            notifier,
            NullLogger<RecordVanSalesOrderDeliveryHandler>.Instance);

        var result = await handler.Handle(
            new RecordVanSalesOrderDeliveryCommand(
                orderId,
                lines.Select(l => new RecordVanSalesDeliveryLine(l.LineNumber, l.Quantity)).ToList(),
                null),
            default);

        _context.ChangeTracker.Clear();
        return result;
    }

    private async Task<ErrorOr.ErrorOr<VanSalesRouteLoadResult>> LoadListAsync(DateTime visitDate)
    {
        var handler = new GetVanSalesOrdersForRouteHandler(_context);

        var result = await handler.Handle(
            new GetVanSalesOrdersForRouteQuery("BP-1", null, visitDate, null),
            default);

        _context.ChangeTracker.Clear();
        return result;
    }

    private Task<int> GivenOrderAsync(params (string ItemCode, decimal Quantity)[] lines)
        => GivenOrderAsync(DateTime.UtcNow.Date.AddDays(1), "CUST-1", lines);

    private async Task<int> GivenOrderAsync(
        DateTime visitDate,
        string customerCode,
        params (string ItemCode, decimal Quantity)[] lines)
    {
        var routeCustomer = await _context.RouteCustomers
            .FirstOrDefaultAsync(c => c.Code == customerCode);

        if (routeCustomer is null)
        {
            routeCustomer = new RouteCustomerEntity
            {
                AssignedBusinessPartnerCode = "BP-1",
                Code = customerCode,
                Name = $"Shop {customerCode}",
                IsActive = true
            };
            _context.RouteCustomers.Add(routeCustomer);
            await _context.SaveChangesAsync();

            _context.VanSalesCustomerAccounts.Add(new VanSalesCustomerAccountEntity
            {
                RouteCustomerId = routeCustomer.Id,
                PhoneE164 = $"+26377{routeCustomer.Id:D7}",
                IsActive = true
            });
            await _context.SaveChangesAsync();
        }

        var account = await _context.VanSalesCustomerAccounts
            .AsNoTracking()
            .FirstAsync(a => a.RouteCustomerId == routeCustomer.Id);

        var order = new VanSalesOrderEntity
        {
            OrderNumber = $"VSO-TEST-{Guid.NewGuid():N}"[..20],
            VanSalesCustomerAccountId = account.Id,
            RouteCustomerId = routeCustomer.Id,
            RouteCustomerCode = routeCustomer.Code,
            RouteCustomerName = routeCustomer.Name,
            AssignedBusinessPartnerCode = "BP-1",
            RouteCode = "GUR",
            RequestedVisitDate = visitDate,
            Status = VanSalesOrderStatus.Accepted,
            Currency = "USD",
            ClientRequestId = Guid.NewGuid().ToString(),
            ReceivedAtUtc = DateTime.UtcNow
        };

        var lineNumber = 1;

        foreach (var (itemCode, quantity) in lines)
        {
            order.Lines.Add(new VanSalesOrderLineEntity
            {
                LineNumber = lineNumber++,
                ItemCode = itemCode,
                ItemDescription = itemCode,
                UoMCode = "EA",
                QuantityOrdered = quantity,
                UnitPrice = 2m,
                TaxPercent = 15.5m,
                LineTotal = quantity * 2m
            });
        }

        order.SubTotal = order.Lines.Sum(l => l.LineTotal);
        order.DocTotal = order.SubTotal;

        _context.VanSalesOrders.Add(order);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        return order.Id;
    }

    private sealed class RecordingNotifier : IVanSalesCustomerNotifier
    {
        public List<VanSalesOrderStatus> Notified { get; } = [];

        public Task NotifyOrderStatusAsync(VanSalesOrderEntity order, CancellationToken cancellationToken)
        {
            Notified.Add(order.Status);
            return Task.CompletedTask;
        }
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
