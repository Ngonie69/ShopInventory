using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.AppVersion;
using ShopInventory.Features.Merchandiser.Queries.GetMobileOrders;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The mobile order list can be narrowed to one customer.
/// </summary>
/// <remarks>
/// Search covers order numbers, customer references and SAP document numbers — never card codes.
/// So the customer detail screen had no way to ask for one customer's orders, and instead paged
/// through the rep's whole history and filtered on the device. These tests exist so the card code
/// filter stays an exact, case-insensitive match that cannot be widened into a wildcard.
/// </remarks>
public sealed class MobileOrderCustomerFilterTests : IDisposable
{
    private static readonly Guid Rep = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherRep = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public MobileOrderCustomerFilterTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new SqliteApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);
        _context.Database.EnsureCreated();

        _context.Users.AddRange(GivenUser(Rep, "rep"), GivenUser(OtherRep, "other-rep"));
        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Returns_every_order_when_no_card_code_is_given()
    {
        await GivenOrder("MOB-1", "TMP119");
        await GivenOrder("MOB-2", "TMP220");

        var result = await HandleAsync(cardCode: null);

        Assert.False(result.IsError);
        Assert.Equal(2, result.Value.TotalCount);
    }

    [Fact]
    public async Task Narrows_the_list_to_one_customer()
    {
        await GivenOrder("MOB-1", "TMP119");
        await GivenOrder("MOB-2", "TMP220");
        await GivenOrder("MOB-3", "TMP119");

        var result = await HandleAsync(cardCode: "TMP119");

        Assert.False(result.IsError);
        Assert.Equal(2, result.Value.TotalCount);
        Assert.All(result.Value.Orders, order => Assert.Equal("TMP119", order.CardCode));
    }

    [Fact]
    public async Task Matches_a_card_code_the_device_cached_in_another_case()
    {
        await GivenOrder("MOB-1", "TMP119");

        var result = await HandleAsync(cardCode: "tmp119");

        Assert.False(result.IsError);
        var order = Assert.Single(result.Value.Orders);
        Assert.Equal("MOB-1", order.OrderNumber);
    }

    [Fact]
    public async Task Trims_a_padded_card_code()
    {
        await GivenOrder("MOB-1", "TMP119");

        var result = await HandleAsync(cardCode: "  TMP119  ");

        Assert.False(result.IsError);
        Assert.Single(result.Value.Orders);
    }

    [Fact]
    public async Task Treats_a_wildcard_as_a_literal_rather_than_matching_everything()
    {
        // The code is an exact identifier arriving from a device. If it were pushed through a LIKE
        // it would widen the filter instead of narrowing it, which is the opposite of the point.
        await GivenOrder("MOB-1", "TMP119");
        await GivenOrder("MOB-2", "TMP220");

        var result = await HandleAsync(cardCode: "%");

        Assert.False(result.IsError);
        Assert.Empty(result.Value.Orders);
    }

    [Fact]
    public async Task Still_will_not_cross_into_another_reps_orders()
    {
        await GivenOrder("MOB-1", "TMP119", createdBy: OtherRep);

        var result = await HandleAsync(cardCode: "TMP119");

        Assert.False(result.IsError);
        Assert.Empty(result.Value.Orders);
    }

    private async Task<ErrorOr.ErrorOr<ShopInventory.DTOs.SalesOrderListResponseDto>> HandleAsync(string? cardCode)
    {
        var handler = new GetMobileOrdersHandler(_context, new NoOpAuditService(), CompatibilityService());

        return await handler.Handle(
            new GetMobileOrdersQuery(
                UserId: Rep,
                Page: 1,
                PageSize: 50,
                Status: null,
                FromDate: null,
                ToDate: null,
                Search: null,
                CardCode: cardCode),
            CancellationToken.None);
    }

    private static MobileOrderStatusCompatibilityService CompatibilityService()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-App-Platform"] = "Android";
        httpContext.Request.Headers["X-App-Version"] = "99.0.0";

        return new MobileOrderStatusCompatibilityService(
            new HttpContextAccessor { HttpContext = httpContext },
            new StaticOptionsMonitor<MobileVersionPolicyOptions>(new MobileVersionPolicyOptions()),
            NullLogger<MobileOrderStatusCompatibilityService>.Instance);
    }

    private static ShopInventory.Models.User GivenUser(Guid id, string username) => new()
    {
        Id = id,
        Username = username,
        PasswordHash = "not-a-real-hash",
        Role = ShopInventory.Models.ApplicationRoles.Merchandiser
    };

    private async Task GivenOrder(string orderNumber, string cardCode, Guid? createdBy = null)
    {
        _context.SalesOrders.Add(new SalesOrderEntity
        {
            OrderNumber = orderNumber,
            CardCode = cardCode,
            CardName = $"Customer {cardCode}",
            OrderDate = DateTime.UtcNow.Date,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            CreatedByUserId = createdBy ?? Rep,
            Source = SalesOrderSource.Mobile,
            Status = SalesOrderStatus.Pending,
            IsSynced = false,
            RowVersion = BitConverter.GetBytes(1L)
        });

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private sealed class NoOpAuditService : IAuditService
    {
        public Task LogAsync(string action, string username, string userRole, string? entityType = null,
            string? entityId = null, string? details = null, string? endpoint = null,
            bool isSuccess = true, string? errorMessage = null) => Task.CompletedTask;

        public Task LogAsync(string action, string? entityType = null, string? entityId = null) => Task.CompletedTask;

        public Task LogAsync(string action, string? entityType, string? entityId, string? details,
            bool isSuccess, string? errorMessage = null) => Task.CompletedTask;
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;

        public T Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<T, string?> listener) => NullDisposable.Instance;

        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();

            public void Dispose()
            {
            }
        }
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
