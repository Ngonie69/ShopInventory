using ErrorOr;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopIntegration.Commands.CreateTransferRequest;
using ShopInventory.Features.VanSalesCompatibility.Commands.CreateVanSalesTransferRequest;
using ShopInventory.Models;

namespace ShopInventory.Tests;

/// <summary>
/// Where a van's stock request is aimed. A van is loaded at one depot and one only — the Bulawayo vans
/// at KEFBYC, the Harare routes at KEFGRC — so the depot belongs on the account, not in the payload.
/// <para>
/// The handset used to choose it from a hardcoded list holding a single entry, "Graniteside Center":
/// a warehouse *name*, not a code SAP would accept, and the wrong depot for half the fleet either way.
/// These pin the source to the assignment and prove the handset can no longer influence it.
/// </para>
/// </summary>
public sealed class VanSalesSupplyingWarehouseTests : IDisposable
{
    private static readonly Guid BulawayoVan = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public VanSalesSupplyingWarehouseTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new ApplicationDbContext(options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private async Task<User> AddVanAsync(
        string vanWarehouse = "VAN010",
        string? supplyingWarehouse = "KEFBYC")
    {
        var user = new User
        {
            Id = BulawayoVan,
            Username = "van010",
            Email = "van010@example.com",
            PasswordHash = "x",
            Role = "Sales",
            IsActive = true,
            AssignedWarehouseCode = vanWarehouse,
            AssignedBusinessPartnerCode = "VAN010",
            AssignedCostCentreCode = "CC010",
            SupplyingWarehouseCode = supplyingWarehouse
        };

        _context.Users.Add(user);
        await _context.SaveChangesAsync();
        return user;
    }

    private static VanSalesTransferRequest BuildRequest(string handsetWarehouse = "Graniteside Center") =>
        new()
        {
            Branch = 1,
            Warehouse = handsetWarehouse,
            DocDate = "2026/08/12",
            Items = [new VanSalesTransferRequestItem { Code = "CHE001", Quantity = 6, Price = 3.5 }]
        };

    private async Task<(ErrorOr<VanSalesTransferRequestResponse> Result, RecordingMediator Mediator)> RequestStockAsync(
        VanSalesTransferRequest request)
    {
        var mediator = new RecordingMediator();
        var handler = new CreateVanSalesTransferRequestHandler(_context, mediator);

        var result = await handler.Handle(
            new CreateVanSalesTransferRequestCommand(request, BulawayoVan),
            CancellationToken.None);

        return (result, mediator);
    }

    /// <summary>
    /// The whole point: a Bulawayo van draws on the Bulawayo depot, whatever the handset put in the
    /// payload. Aimed at Graniteside, the stock would be picked 440km from the van waiting for it.
    /// </summary>
    [Fact]
    public async Task The_source_is_the_depot_assigned_to_the_van()
    {
        await AddVanAsync(supplyingWarehouse: "KEFBYC");

        var (result, mediator) = await RequestStockAsync(BuildRequest(handsetWarehouse: "Graniteside Center"));

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.Equal("KEFBYC", mediator.LastTransferRequest.FromWarehouse);
        Assert.Equal("VAN010", mediator.LastTransferRequest.ToWarehouse);
    }

    /// <summary>
    /// SAP reads the warehouses off each line, not only the header, so a line left pointing at the
    /// handset's value would move the stock out of the wrong depot however right the header looked.
    /// </summary>
    [Fact]
    public async Task Every_line_draws_on_the_same_depot_as_the_header()
    {
        await AddVanAsync(supplyingWarehouse: "KEFBYC");

        var (_, mediator) = await RequestStockAsync(BuildRequest());

        Assert.All(mediator.LastTransferRequest.Lines, line =>
        {
            Assert.Equal("KEFBYC", line.FromWarehouseCode);
            Assert.Equal("VAN010", line.ToWarehouseCode);
        });
    }

    /// <summary>
    /// A handset that sends nothing at all is no different: the field is not consulted either way.
    /// It used to be rejected as a missing source, which would now refuse a request the server can
    /// answer perfectly well on its own.
    /// </summary>
    [Fact]
    public async Task A_handset_that_sends_no_warehouse_is_still_served()
    {
        await AddVanAsync(supplyingWarehouse: "KEFGRC");

        var (result, mediator) = await RequestStockAsync(BuildRequest(handsetWarehouse: ""));

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.Equal("KEFGRC", mediator.LastTransferRequest.FromWarehouse);
    }

    /// <summary>
    /// Nothing is guessed for an unassigned van. A default would send half the fleet's requests to the
    /// wrong depot silently, which is the failure this change exists to end.
    /// </summary>
    [Fact]
    public async Task An_unassigned_van_is_refused_rather_than_guessed_for()
    {
        await AddVanAsync(supplyingWarehouse: null);

        var (result, mediator) = await RequestStockAsync(BuildRequest());

        Assert.True(result.IsError);
        Assert.Equal("VanSalesCompatibility.MissingSourceWarehouse", result.FirstError.Code);
        Assert.Empty(mediator.Sent);
    }

    /// <summary>
    /// A van assigned its own warehouse as its depot would raise a request that moves nothing. Caught
    /// here rather than left for SAP, so the message names the account that needs correcting.
    /// </summary>
    [Fact]
    public async Task A_van_pointed_at_itself_is_refused()
    {
        await AddVanAsync(vanWarehouse: "VAN010", supplyingWarehouse: "van010");

        var (result, mediator) = await RequestStockAsync(BuildRequest());

        Assert.True(result.IsError);
        Assert.Equal("VanSalesCompatibility.SourceIsDestination", result.FirstError.Code);
        Assert.Empty(mediator.Sent);
    }

    /// <summary>
    /// Padding on the assignment must not reach SAP as part of the code — a warehouse " KEFBYC " does
    /// not exist, and the warehouse check would refuse the whole request.
    /// </summary>
    [Fact]
    public async Task An_assignment_padded_by_hand_is_trimmed()
    {
        await AddVanAsync(supplyingWarehouse: "  KEFBYC  ");

        var (_, mediator) = await RequestStockAsync(BuildRequest());

        Assert.Equal("KEFBYC", mediator.LastTransferRequest.FromWarehouse);
    }

    /// <summary>
    /// The handset dates its request yyyy/MM/dd. SAP takes ISO and nothing else, and answered the
    /// slashes with "Invalid date format in property 'DocDate' of 'StockTransfer'" — the request
    /// never reached the depot. Both dates are checked because SAP validates them separately.
    /// </summary>
    [Fact]
    public async Task A_handset_date_is_rewritten_as_the_ISO_date_SAP_accepts()
    {
        await AddVanAsync(supplyingWarehouse: "KEFBYC");

        var (_, mediator) = await RequestStockAsync(BuildRequest());

        Assert.Equal("2026-08-12", mediator.LastTransferRequest.DocDate);
        Assert.Equal("2026-08-12", mediator.LastTransferRequest.DueDate);
    }

    /// <summary>Records the transfer request the van sales layer delegated, without posting it.</summary>
    private sealed class RecordingMediator : IMediator
    {
        public List<object> Sent { get; } = [];

        public CreateDesktopTransferRequestDto LastTransferRequest =>
            Sent.OfType<CreateTransferRequestCommand>().Last().Request;

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Sent.Add(request);

            // Only the DocEntry and DocNum are read back off this, so an empty document stands in for
            // whatever SAP would have returned.
            var responseType = typeof(TResponse);
            var valueType = responseType.GetGenericArguments()[0];
            var value = Activator.CreateInstance(valueType)!;

            var response = responseType.GetMethod("op_Implicit", [valueType])!.Invoke(null, [value])!;

            return Task.FromResult((TResponse)response);
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest
            => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification
            => throw new NotSupportedException();
    }
}
