using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesSalesOrderHistory;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Which sales orders a van sales handset is shown.
/// </summary>
/// <remarks>
/// <para>The screen is titled "Sales orders" and a rep opens it to convert one into an invoice. It
/// used to answer a narrower question than that — the orders <em>this account</em> had itself keyed
/// <em>on a handset</em> — because the read was filtered on <c>CreatedByUserId</c> and
/// <c>Source == Mobile</c>. So an order taken by whoever had the van yesterday, or keyed at the
/// depot, could not be converted by the rep standing in the shop today.</para>
///
/// <para>What makes the business partner the right scope is where it is written. For a van-raised
/// order <see cref="SalesOrderEntity.CardCode"/> holds the <em>van's own</em> business partner —
/// that is the account SAP bills — and the shop is recorded separately in
/// <see cref="SalesOrderEntity.RouteCustomerCode"/>. Scoping on <c>CardCode</c> is therefore exactly
/// "the orders standing against this van's account", whoever keyed them.</para>
///
/// <para>The half that has to hold while the other is relaxed is the same one the invoice read
/// states: the customer scope is now the <em>only</em> narrowing, so a scope that resolves to
/// nothing must show nothing rather than everything.
/// <see cref="An_account_with_no_customer_scope_is_shown_nothing"/> is the guard for that.</para>
/// </remarks>
public sealed class VanSalesSalesOrderHistoryScopeTests : IDisposable
{
    private static readonly Guid Rep = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid OtherRep = Guid.Parse("55555555-5555-5555-5555-555555555555");

    /// <summary>The business partner the van is assigned to. Every shop on its route bills here.</summary>
    private const string VanBusinessPartner = "C-VAN-014";

    /// <summary>Another van's account, which this rep may not read.</summary>
    private const string OtherBusinessPartner = "C-VAN-099";

    private const string WindowStart = "2026-08-10";
    private const string WindowEnd = "2026-08-24";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public VanSalesSalesOrderHistoryScopeTests()
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

    // ── The regression: an order this rep did not personally key ────────────

    [Fact]
    public async Task An_order_keyed_by_another_rep_on_the_same_business_partner_is_listed()
    {
        // Yesterday's rep took the order on the same van. The shop is expecting it invoiced today, by
        // whoever is holding the handset — which is the whole reason this screen exists.
        GivenVanRep();
        GivenSalesOrder(id: 5001, orderNumber: "SO-5001", createdBy: OtherRep);

        var history = await WhenHistoryIsRead();

        var order = Assert.Single(history);
        Assert.Equal(5001, order.Id);
    }

    [Fact]
    public async Task An_order_raised_away_from_a_handset_is_listed()
    {
        // Keyed at the depot rather than on the van. It stands against the same account and is the
        // same document to convert.
        GivenVanRep();
        GivenSalesOrder(id: 5002, orderNumber: "SO-5002", createdBy: null, source: SalesOrderSource.Web);

        var history = await WhenHistoryIsRead();

        Assert.Equal(5002, Assert.Single(history).Id);
    }

    [Fact]
    public async Task An_order_the_shop_placed_itself_is_listed()
    {
        // The ordering app's whole point is that the shop raises its own demand. A rep calling on that
        // shop is who turns it into an invoice.
        GivenVanRep();
        GivenSalesOrder(id: 5003, orderNumber: "SO-5003", createdBy: null, source: SalesOrderSource.VanSalesCustomer);

        Assert.Equal(5003, Assert.Single(await WhenHistoryIsRead()).Id);
    }

    // ── The narrowing that has to hold ──────────────────────────────────────

    [Fact]
    public async Task An_account_with_no_customer_scope_is_shown_nothing()
    {
        // The rule this pins is "nothing, never everything". With CreatedByUserId gone, a scope that
        // resolves to no codes and is read as "no filter" hands this handset every sales order the
        // company holds in the window — including other vans' accounts.
        GivenVanRep(businessPartner: null);
        GivenSalesOrder(id: 5004, orderNumber: "SO-5004", createdBy: Rep);
        GivenSalesOrder(id: 5005, orderNumber: "SO-5005", createdBy: OtherRep, cardCode: OtherBusinessPartner);

        Assert.Empty(await WhenHistoryIsRead());
    }

    [Fact]
    public async Task An_order_against_another_business_partner_is_not_listed()
    {
        GivenVanRep();
        GivenSalesOrder(id: 5006, orderNumber: "SO-5006", createdBy: Rep, cardCode: OtherBusinessPartner);

        Assert.Empty(await WhenHistoryIsRead());
    }

    [Fact]
    public async Task An_order_outside_the_window_is_not_listed()
    {
        GivenVanRep();
        GivenSalesOrder(
            id: 5007,
            orderNumber: "SO-5007",
            createdBy: Rep,
            orderedAt: new DateTime(2026, 7, 1, 9, 0, 0, DateTimeKind.Utc));

        Assert.Empty(await WhenHistoryIsRead());
    }

    [Theory]
    [InlineData(SalesOrderStatus.Draft)]
    [InlineData(SalesOrderStatus.Pending)]
    [InlineData(SalesOrderStatus.Approved)]
    public async Task An_order_is_listed_whatever_its_status(SalesOrderStatus status)
    {
        // Filtering this list to Approved was tried, on the reasoning that conversion refuses anything
        // else so listing the rest only misleads. It is wrong, and a handset settled it: an order is
        // written Pending and nothing on the device approves it, so SO3442 — raised from a handset on
        // 2026-09-09 and stored as status 1 — was invisible on the screen a minute after it was taken.
        //
        // Losing a rep the record of an order they just took is worse than letting them meet the
        // refusal, and the refusal belongs on the row as a status rather than here as an omission.
        GivenVanRep();
        GivenSalesOrder(id: 5009, orderNumber: "SO-5009", createdBy: OtherRep, status: status);

        Assert.Equal(5009, Assert.Single(await WhenHistoryIsRead()).Id);
    }

    // ── The contract the handset reads ──────────────────────────────────────

    [Fact]
    public async Task A_listed_order_is_typed_SO_and_carries_its_lines()
    {
        // The handset filters this list on type == "SO" before drawing it, so a row typed anything
        // else is a row the rep never sees. Pinned here because the filter is on the other side of an
        // API boundary and nothing else asserts the two agree.
        GivenVanRep();
        GivenSalesOrder(id: 5008, orderNumber: "SO-5008", createdBy: OtherRep, withLines: true);

        var order = Assert.Single(await WhenHistoryIsRead());

        Assert.Equal("SO", order.Type);
        Assert.Equal(2, order.Item);
        Assert.Collection(
            order.OrderItems,
            first =>
            {
                Assert.Equal("MOZ-1KG", first.Code);
                Assert.Equal("Mozzarella 1kg", first.Name);
                Assert.Equal(3, first.Quantity);
            },
            second =>
            {
                Assert.Equal("FET-500", second.Code);
                Assert.Equal(2, second.Quantity);
            });
    }

    // ── Given ───────────────────────────────────────────────────────────────

    private void GivenVanRep(string? businessPartner = VanBusinessPartner)
    {
        _context.Users.Add(new User
        {
            Id = Rep,
            Username = "van-rep",
            PasswordHash = "not-a-real-hash",
            Role = ApplicationRoles.Sales,
            IsActive = true,
            AssignedBusinessPartnerCode = businessPartner
        });

        // Whoever had the handset yesterday. Scenery rather than the subject of any assertion, but the
        // orders below point at them, and CreatedByUserId is a foreign key.
        _context.Users.Add(new User
        {
            Id = OtherRep,
            Username = "van-rep-yesterday",
            PasswordHash = "not-a-real-hash",
            Role = ApplicationRoles.Sales,
            IsActive = true,
            AssignedBusinessPartnerCode = businessPartner
        });

        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    private void GivenSalesOrder(
        int id,
        string orderNumber,
        Guid? createdBy,
        string cardCode = VanBusinessPartner,
        SalesOrderSource source = SalesOrderSource.Mobile,
        DateTime? orderedAt = null,
        bool withLines = false,
        SalesOrderStatus status = SalesOrderStatus.Approved)
    {
        var order = new SalesOrderEntity
        {
            Id = id,
            OrderNumber = orderNumber,
            // ApplicationDbContext refuses to save an Approved order without one, and an order a rep
            // can actually convert has been to SAP and come back with a number.
            SAPDocEntry = id,
            SAPDocNum = id,
            CardCode = cardCode,
            CardName = "Shop on the route",
            OrderDate = orderedAt ?? new DateTime(2026, 8, 20, 9, 0, 0, DateTimeKind.Utc),
            CreatedAt = orderedAt ?? new DateTime(2026, 8, 20, 9, 0, 0, DateTimeKind.Utc),
            Source = source,
            CreatedByUserId = createdBy,
            Status = status,
            Currency = "USD",
            SubTotal = 100m,
            TaxAmount = 15m,
            DocTotal = 115m,
            RowVersion = BitConverter.GetBytes(1L)
        };

        if (withLines)
        {
            order.Lines =
            [
                new SalesOrderLineEntity
                {
                    LineNum = 0,
                    ItemCode = "MOZ-1KG",
                    ItemDescription = "Mozzarella 1kg",
                    Quantity = 3m,
                    UnitPrice = 20m,
                    LineTotal = 60m
                },
                new SalesOrderLineEntity
                {
                    LineNum = 1,
                    ItemCode = "FET-500",
                    ItemDescription = "Feta 500g",
                    Quantity = 2m,
                    UnitPrice = 20m,
                    LineTotal = 40m
                }
            ];
        }

        _context.SalesOrders.Add(order);
        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    // ── When ────────────────────────────────────────────────────────────────

    private async Task<List<VanSalesLegacyOrderDto>> WhenHistoryIsRead()
    {
        var handler = new GetVanSalesSalesOrderHistoryHandler(
            _context,
            NullLogger<GetVanSalesSalesOrderHistoryHandler>.Instance);

        var result = await handler.Handle(
            new GetVanSalesSalesOrderHistoryQuery(
                Rep,
                new VanSalesOrderSearchRequest
                {
                    Type = "SO",
                    StartDate = WindowStart,
                    EndDate = WindowEnd
                }),
            CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? string.Join("; ", result.Errors) : string.Empty);
        return result.Value;
    }

    /// <summary>
    /// <see cref="SalesOrderEntity.RowVersion"/> is <c>[Timestamp]</c>, which Npgsql maps to the
    /// store-generated <c>xmin</c> system column. SQLite has no equivalent, so EF leaves the column
    /// out of the INSERT and the NOT NULL constraint fails.
    /// </summary>
    private sealed class SqliteApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : ApplicationDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<SalesOrderEntity>()
                .Property(order => order.RowVersion)
                .ValueGeneratedNever()
                .IsConcurrencyToken(false);
        }
    }
}
