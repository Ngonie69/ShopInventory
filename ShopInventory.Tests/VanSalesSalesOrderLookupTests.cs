using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Maintenance;
using ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesSalesOrderByVanOrder;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// <c>GET api/vansales/sales-order/{vanOrder}</c> — a handset asking whether the sales order its lost reply
/// was about landed.
/// </summary>
/// <remarks>
/// The handset binds this by name, so the JSON is asserted as text: a renamed property is a field it reads
/// as null. And as with the sale lookup, "not there" and "not yours" must be the same 200 — anything else
/// reads to the handset as "could not check".
/// </remarks>
public sealed class VanSalesSalesOrderLookupTests : IDisposable
{
    private static readonly Guid Rep = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid SecondRep = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid OtherVanRep = Guid.Parse("88888888-8888-8888-8888-888888888888");

    private const string VanBusinessPartner = "C-VAN-014";
    private const string OtherVanBusinessPartner = "C-VAN-022";
    private const string VanOrder = "VAN014-SO-20261021-AB12CD";

    private static readonly JsonSerializerOptions ApiJson = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public VanSalesSalesOrderLookupTests()
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
    public async Task An_order_that_landed_answers_with_its_number_and_status()
    {
        GivenRep(Rep, VanBusinessPartner);
        var orderId = GivenOrder(VanOrder, createdBy: Rep);

        var answer = await Lookup(Rep, VanOrder);

        Assert.Equal(
            "{" +
            "\"found\":true," +
            $"\"van_order\":\"{VanOrder}\"," +
            $"\"order_id\":{orderId}," +
            "\"order_number\":\"SO-20261021-0001\"," +
            "\"status\":\"Pending\"," +
            "\"sap_doc_num\":null," +
            $"\"card_code\":\"{VanBusinessPartner}\"," +
            "\"card_name\":\"Mai Tendai Tuckshop\"," +
            "\"total\":23.68," +
            "\"currency\":\"USD\"," +
            "\"created_at_utc\":\"2026-10-21T08:12:00Z\"" +
            "}",
            JsonSerializer.Serialize(answer, ApiJson));
    }

    [Fact]
    public async Task A_van_order_with_no_order_is_found_false_with_everything_else_empty()
    {
        GivenRep(Rep, VanBusinessPartner);

        var answer = await Lookup(Rep, "VAN014-SO-20261021-NOSUCH");

        Assert.Equal(
            "{" +
            "\"found\":false," +
            "\"van_order\":\"VAN014-SO-20261021-NOSUCH\"," +
            "\"order_id\":null," +
            "\"order_number\":null," +
            "\"status\":null," +
            "\"sap_doc_num\":null," +
            "\"card_code\":null," +
            "\"card_name\":null," +
            "\"total\":null," +
            "\"currency\":null," +
            "\"created_at_utc\":null" +
            "}",
            JsonSerializer.Serialize(answer, ApiJson));
    }

    [Fact]
    public async Task The_van_order_is_matched_as_the_post_wrote_it_trimmed()
    {
        GivenRep(Rep, VanBusinessPartner);
        GivenOrder(VanOrder, createdBy: Rep);

        var answer = await Lookup(Rep, $"  {VanOrder} ");

        Assert.True(answer.Found);
        Assert.Equal(VanOrder, answer.VanOrder);
    }

    [Fact]
    public async Task Another_vans_order_answers_as_not_found()
    {
        GivenRep(Rep, VanBusinessPartner);
        GivenRep(OtherVanRep, OtherVanBusinessPartner);
        GivenOrder(VanOrder, createdBy: OtherVanRep, cardCode: OtherVanBusinessPartner);

        var answer = await Lookup(Rep, VanOrder);

        Assert.False(answer.Found);
        Assert.Null(answer.OrderNumber);
        Assert.Null(answer.CardCode);
    }

    [Fact]
    public async Task A_second_rep_on_the_same_van_sees_the_order()
    {
        GivenRep(Rep, VanBusinessPartner);
        GivenRep(SecondRep, VanBusinessPartner);
        GivenOrder(VanOrder, createdBy: Rep);

        var answer = await Lookup(SecondRep, VanOrder);

        Assert.True(answer.Found);
    }

    [Fact]
    public async Task A_van_order_longer_than_the_column_is_not_found_rather_than_refused()
    {
        GivenRep(Rep, VanBusinessPartner);

        var answer = await Lookup(Rep, new string('X', 201));

        Assert.False(answer.Found);
    }

    [Fact]
    public async Task An_inactive_account_is_refused()
    {
        GivenRep(Rep, VanBusinessPartner, isActive: false);

        var result = await Handler().Handle(new GetVanSalesSalesOrderByVanOrderQuery(Rep, VanOrder), default);

        Assert.True(result.IsError);
    }

    [Fact]
    public void The_route_is_a_read_to_the_maintenance_gate()
    {
        Assert.True(MaintenanceGate.IsRead("GET", $"/api/vansales/sales-order/{VanOrder}"));
    }

    private GetVanSalesSalesOrderByVanOrderHandler Handler() => new(
        _context, NullLogger<GetVanSalesSalesOrderByVanOrderHandler>.Instance);

    private async Task<VanSalesSalesOrderLookupResponse> Lookup(Guid userId, string vanOrder)
    {
        var result = await Handler().Handle(new GetVanSalesSalesOrderByVanOrderQuery(userId, vanOrder), default);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        return result.Value;
    }

    private void GivenRep(Guid id, string businessPartner, bool isActive = true)
    {
        _context.Users.Add(new User
        {
            Id = id,
            Username = $"van-rep-{id:N}",
            PasswordHash = "not-a-real-hash",
            Role = ApplicationRoles.Sales,
            IsActive = isActive,
            AssignedBusinessPartnerCode = businessPartner
        });

        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    /// <summary>An order as the van post writes it: the reference trimmed into <c>ClientRequestId</c>.</summary>
    private int GivenOrder(string reference, Guid createdBy, string cardCode = VanBusinessPartner)
    {
        var order = new SalesOrderEntity
        {
            OrderNumber = "SO-20261021-0001",
            OrderDate = new DateTime(2026, 10, 21, 8, 12, 0, DateTimeKind.Utc),
            CardCode = cardCode,
            CardName = "Mai Tendai Tuckshop",
            Currency = "USD",
            Status = SalesOrderStatus.Pending,
            Source = SalesOrderSource.Mobile,
            DocTotal = 23.68m,
            ClientRequestId = reference,
            CreatedByUserId = createdBy,
            CreatedAt = new DateTime(2026, 10, 21, 8, 12, 0, DateTimeKind.Utc),
            Lines = [new SalesOrderLineEntity { LineNum = 0, ItemCode = "FRM001", Quantity = 2m, UnitPrice = 11.84m, LineTotal = 23.68m }],
            RowVersion = BitConverter.GetBytes(1L)
        };

        _context.SalesOrders.Add(order);
        _context.SaveChanges();
        _context.ChangeTracker.Clear();
        return order.Id;
    }

    /// <summary>
    /// <see cref="SalesOrderEntity.RowVersion"/> is Npgsql's store-generated <c>xmin</c>; SQLite has no
    /// equivalent, so it is written by hand here (as MobileOrderClientRequestLookupTests does).
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
