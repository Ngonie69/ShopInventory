using ErrorOr;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopIntegration.Commands.ConvertSalesOrderToInvoice;
using ShopInventory.Features.VanSalesCompatibility.Commands.ConvertVanSalesSalesOrderToInvoice;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Which sales orders a van sales handset may convert into an invoice.
/// </summary>
/// <remarks>
/// <para>The endpoint takes a sales order id out of the request body, and nothing downstream reads
/// the caller's scope — <c>ConvertSalesOrderToInvoiceHandler</c> fetches by id and asks only whether
/// the order exists, is approved and has lines. So the id was the whole authorisation, and sales
/// order ids are sequential integers.</para>
///
/// <para>What that costs is worth stating, because it is not simply "reads another van's order".
/// The invoice bills the <em>order's</em> <c>CardCode</c> — the other van's business partner — while
/// the lines are drawn against <c>ResolveAssignedWarehouseCode(user)</c>, which is the caller's own
/// van. So one van's stock leaves and another van's account is billed, and it is fiscalised on the
/// way out, which is the half that cannot be undone by anything but a credit note.</para>
///
/// <para>The refusal is deliberately worded as "not found" rather than "not yours"; see
/// <see cref="A_refusal_does_not_say_whether_the_order_exists"/>.</para>
/// </remarks>
public sealed class VanSalesConversionScopeTests : IDisposable
{
    private static readonly Guid Rep = Guid.Parse("66666666-6666-6666-6666-666666666666");

    private const string VanBusinessPartner = "C-VAN-014";
    private const string OtherBusinessPartner = "C-VAN-099";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly RecordingMediator _mediator;

    public VanSalesConversionScopeTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new SqliteApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);
        _context.Database.EnsureCreated();
        _mediator = new RecordingMediator();

        _context.Users.Add(new User
        {
            Id = Rep,
            Username = "van-rep",
            PasswordHash = "not-a-real-hash",
            Role = ApplicationRoles.Sales,
            IsActive = true,
            AssignedBusinessPartnerCode = VanBusinessPartner,
            AssignedWarehouseCodes = """["VAN014"]""",
            AssignedCostCentreCode = "CC-VAN"
        });

        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // ── The gap this closes ─────────────────────────────────────────────────

    [Fact]
    public async Task An_order_billing_another_vans_business_partner_is_refused()
    {
        GivenSalesOrder(id: 7001, cardCode: OtherBusinessPartner);

        var result = await WhenConversionIsAsked(salesOrderId: 7001);

        Assert.True(result.IsError);
        Assert.Equal("VanSalesCompatibility.SalesOrderNotFound", result.FirstError.Code);

        // The load-bearing half. An error that arrives after the invoice has been posted is a credit
        // note, not a refusal.
        Assert.Empty(_mediator.Sent);
    }

    [Fact]
    public async Task An_account_with_no_scope_may_convert_nothing()
    {
        GivenRepHasNoScope();
        GivenSalesOrder(id: 7002, cardCode: VanBusinessPartner);

        var result = await WhenConversionIsAsked(salesOrderId: 7002);

        Assert.True(result.IsError);
        Assert.Empty(_mediator.Sent);
    }

    [Fact]
    public async Task A_refusal_does_not_say_whether_the_order_exists()
    {
        // Someone else's order and an id that was never issued have to answer identically, or the
        // endpoint can be walked to find out which ids are real.
        GivenSalesOrder(id: 7003, cardCode: OtherBusinessPartner);

        var someoneElses = await WhenConversionIsAsked(salesOrderId: 7003);
        var neverIssued = await WhenConversionIsAsked(salesOrderId: 7999);

        Assert.Equal(someoneElses.FirstError.Code, neverIssued.FirstError.Code);
        Assert.Empty(_mediator.Sent);
    }

    // ── What must still go through ──────────────────────────────────────────

    [Fact]
    public async Task An_order_on_the_accounts_own_business_partner_is_converted()
    {
        // Keyed by nobody this handset knows — the point of the widened history read is that this is
        // an ordinary order to convert, so the guard must not be a re-run of the old ownership check.
        GivenSalesOrder(id: 7004, cardCode: VanBusinessPartner, createdBy: null);

        var result = await WhenConversionIsAsked(salesOrderId: 7004);

        Assert.False(result.IsError, result.IsError ? string.Join("; ", result.Errors) : string.Empty);

        var sent = Assert.Single(_mediator.Sent);
        Assert.Equal(7004, Assert.IsType<ConvertSalesOrderToInvoiceCommand>(sent).Request.SalesOrderId);
    }

    [Fact]
    public async Task The_scope_is_matched_without_regard_to_case()
    {
        // The codes are stored as they were typed. Refusing a real order over its casing strands a rep
        // at a counter, which is the more expensive of the two mistakes here.
        GivenSalesOrder(id: 7005, cardCode: VanBusinessPartner.ToLowerInvariant());

        var result = await WhenConversionIsAsked(salesOrderId: 7005);

        Assert.False(result.IsError, result.IsError ? string.Join("; ", result.Errors) : string.Empty);
        Assert.Single(_mediator.Sent);
    }

    // ── Given ───────────────────────────────────────────────────────────────

    private void GivenRepHasNoScope()
    {
        var rep = _context.Users.First(user => user.Id == Rep);
        rep.AssignedBusinessPartnerCode = null;
        rep.AssignedCustomerCodes = null;

        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    private void GivenSalesOrder(int id, string cardCode, Guid? createdBy = null)
    {
        _context.SalesOrders.Add(new SalesOrderEntity
        {
            Id = id,
            OrderNumber = $"SO-{id}",
            SAPDocEntry = id,
            SAPDocNum = id,
            CardCode = cardCode,
            CardName = "Shop on the route",
            OrderDate = new DateTime(2026, 8, 20, 9, 0, 0, DateTimeKind.Utc),
            CreatedAt = new DateTime(2026, 8, 20, 9, 0, 0, DateTimeKind.Utc),
            Source = SalesOrderSource.Mobile,
            CreatedByUserId = createdBy,
            Status = SalesOrderStatus.Approved,
            Currency = "USD",
            SubTotal = 100m,
            TaxAmount = 15m,
            DocTotal = 115m,
            RowVersion = BitConverter.GetBytes(1L)
        });

        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    // ── When ────────────────────────────────────────────────────────────────

    private async Task<ErrorOr<VanSalesConvertSalesOrderToInvoiceResponse>> WhenConversionIsAsked(int salesOrderId)
    {
        var handler = new ConvertVanSalesSalesOrderToInvoiceHandler(
            _context,
            _mediator,
            NullLogger<ConvertVanSalesSalesOrderToInvoiceHandler>.Instance);

        return await handler.Handle(
            new ConvertVanSalesSalesOrderToInvoiceCommand(
                new VanSalesOrderRequest { SalesOrderId = salesOrderId, VanOrder = $"VAN-{salesOrderId}" },
                Rep),
            CancellationToken.None);
    }

    /// <summary>
    /// Records what the handler delegated, and confirms nothing. Whether the inner conversion would
    /// have succeeded is not this file's question — whether it was reached at all is.
    /// </summary>
    private sealed class RecordingMediator : IMediator
    {
        public List<object> Sent { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Sent.Add(request);

            var response = new ConvertSalesOrderToInvoiceResponseDto
            {
                Success = true,
                Message = "Queued",
                SalesOrderId = ((ConvertSalesOrderToInvoiceCommand)(object)request).Request.SalesOrderId
            };

            var errorOr = typeof(TResponse)
                .GetMethod("op_Implicit", [typeof(ConvertSalesOrderToInvoiceResponseDto)])!
                .Invoke(null, [response])!;

            return Task.FromResult((TResponse)errorOr);
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TResponse> Send<TRequest, TResponse>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest<TResponse> => throw new NotSupportedException();

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => throw new NotSupportedException();
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
