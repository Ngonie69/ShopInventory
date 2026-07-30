using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.AppVersion;
using ShopInventory.Features.Merchandiser.Queries.GetMobileOrderByClientRequestId;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// The device asks this lookup one question: did my idempotency key already produce an order? It
/// is the only way a draft whose submit timed out — or whose retry the idempotency middleware
/// replayed with a bare message body instead of the order — can be reconciled without the rep
/// keying the order a second time.
/// </summary>
public sealed class MobileOrderClientRequestLookupTests : IDisposable
{
    private static readonly Guid Rep = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherRep = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public MobileOrderClientRequestLookupTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new SqliteApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);
        _context.Database.EnsureCreated();

        // SalesOrders.CreatedByUserId is a foreign key, so both reps have to exist before an order
        // can be attributed to either of them.
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
    public async Task Resolves_the_order_a_key_already_created()
    {
        await GivenOrder("MOB-1", "key-a", Rep);

        var result = await HandleAsync("key-a");

        Assert.False(result.IsError);
        Assert.Equal("MOB-1", result.Value.OrderNumber);
        Assert.Equal("key-a", result.Value.ClientRequestId);
    }

    [Fact]
    public async Task Reports_not_found_when_the_key_never_reached_the_server()
    {
        await GivenOrder("MOB-1", "key-a", Rep);

        var result = await HandleAsync("key-b");

        Assert.True(result.IsError);
        Assert.Equal(ErrorOr.ErrorType.NotFound, result.FirstError.Type);
    }

    [Fact]
    public async Task Trims_the_key_so_a_padded_draft_value_still_resolves()
    {
        await GivenOrder("MOB-1", "key-a", Rep);

        var result = await HandleAsync("  key-a  ");

        Assert.False(result.IsError);
        Assert.Equal("MOB-1", result.Value.OrderNumber);
    }

    [Fact]
    public async Task Will_not_hand_one_rep_another_reps_order()
    {
        // Keys are device-generated GUIDs, so a collision is not the worry — reading someone
        // else's order by guessing a key is.
        await GivenOrder("MOB-1", "key-a", OtherRep);

        var result = await HandleAsync("key-a");

        Assert.True(result.IsError);
        Assert.Equal(ErrorOr.ErrorType.NotFound, result.FirstError.Type);
    }

    [Fact]
    public async Task Rejects_a_blank_key_rather_than_matching_a_keyless_order()
    {
        await GivenOrder("MOB-1", clientRequestId: null, Rep);

        var result = await HandleAsync("   ");

        Assert.True(result.IsError);
        Assert.Equal(ErrorOr.ErrorType.Validation, result.FirstError.Type);
    }

    [Fact]
    public async Task Reports_a_submitted_but_unposted_order_as_pending_not_draft()
    {
        // The reconciling app has to be told the order exists and is awaiting approval. Draft
        // would read as "never sent" and invite a resubmit.
        await GivenOrder("MOB-1", "key-a", Rep, status: SalesOrderStatus.Draft, sapDocNum: null, isSynced: false);

        var result = await HandleAsync("key-a");

        Assert.False(result.IsError);
        Assert.Equal(SalesOrderStatus.Pending, result.Value.Status);
    }

    private async Task<ErrorOr.ErrorOr<ShopInventory.DTOs.SalesOrderDto>> HandleAsync(string clientRequestId)
    {
        var handler = new GetMobileOrderByClientRequestIdHandler(_context, CompatibilityService());
        return await handler.Handle(new GetMobileOrderByClientRequestIdQuery(Rep, clientRequestId), CancellationToken.None);
    }

    /// <summary>
    /// Built on a request carrying the current app version, so the pending-status compatibility
    /// shim stays off and the statuses under test are the modern ones.
    /// </summary>
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

    private async Task GivenOrder(
        string orderNumber,
        string? clientRequestId,
        Guid createdBy,
        SalesOrderStatus status = SalesOrderStatus.Pending,
        int? sapDocNum = null,
        bool isSynced = false)
    {
        _context.SalesOrders.Add(new SalesOrderEntity
        {
            OrderNumber = orderNumber,
            CardCode = "TMP119",
            CardName = "Test customer",
            OrderDate = DateTime.UtcNow.Date,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            CreatedByUserId = createdBy,
            Source = SalesOrderSource.Mobile,
            Status = status,
            SAPDocNum = sapDocNum,
            IsSynced = isSynced,
            ClientRequestId = clientRequestId,
            RowVersion = BitConverter.GetBytes(1L)
        });

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
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
