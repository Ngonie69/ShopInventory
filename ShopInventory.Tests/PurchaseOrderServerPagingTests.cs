using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.Features.PurchaseOrders.Queries.GetPurchaseOrdersFromSAP;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The Purchase Orders page asks for one page, with SAP or the local table doing the filtering,
/// counting and paging, and reads its five figures from the response's summary.
/// </summary>
/// <remarks>
/// It asked for up to ten thousand orders with their lines, held them for as long as the tab stayed
/// open, filtered SAP's by status itself and counted the figures off them. For the SAP source a
/// filtered date range or supplier was read whole on every load and paged in the API.
/// </remarks>
public sealed class PurchaseOrderServerPagingTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly List<(string Method, object?[] Args)> _sapCalls = [];

    public PurchaseOrderServerPagingTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private static readonly DateTime From = new(2026, 9, 1);
    private static readonly DateTime To = new(2026, 9, 30);

    [Fact]
    public async Task SAP_counts_and_pages_with_the_filters_and_the_summary_counts_each_status()
    {
        var result = await HandleSapAsync(new GetPurchaseOrdersFromSAPQuery(3, 25, "S001", From, To, IncludeSummary: true), total: 120);

        var count = _sapCalls.First(call => call.Method == nameof(ISAPServiceLayerClient.CountPurchaseOrdersAsync));
        Assert.Equal(["S001", From, To, null], count.Args.Take(4));

        var page = Assert.Single(_sapCalls, call => call.Method == nameof(ISAPServiceLayerClient.GetPurchaseOrderPageAsync));
        Assert.Equal(["S001", From, To, null, 50, 25], page.Args.Take(6));

        Assert.Equal(120, result.TotalCount);
        Assert.Equal(5, result.TotalPages);
        Assert.Equal(25, result.Orders.Count);

        var summary = result.Summary!;
        Assert.Equal(120, summary.Total);
        Assert.Equal(0, summary.Draft);
        Assert.Equal(0, summary.Pending);
        // The stub answers a status count with the filter's length, so each figure names its own filter.
        PurchaseOrderSapStatus.TryGetFilter(PurchaseOrderStatus.Approved, out var approved);
        PurchaseOrderSapStatus.TryGetFilter(PurchaseOrderStatus.Received, out var received);
        Assert.Equal(approved!.Length, summary.Approved);
        Assert.Equal(received!.Length, summary.Received);
    }

    [Fact]
    public async Task A_SAP_status_filter_goes_to_SAP_and_its_figure_is_the_total()
    {
        var result = await HandleSapAsync(new GetPurchaseOrdersFromSAPQuery(1, 10, null, null, null, PurchaseOrderStatus.Received, IncludeSummary: true), total: 7);

        PurchaseOrderSapStatus.TryGetFilter(PurchaseOrderStatus.Received, out var received);
        Assert.All(_sapCalls, call => Assert.Equal(received, call.Args[3]));
        Assert.Single(_sapCalls, call => call.Method == nameof(ISAPServiceLayerClient.CountPurchaseOrdersAsync));
        Assert.Equal(7, result.Summary!.Received);
        Assert.Equal(0, result.Summary.Approved);
    }

    [Theory]
    [InlineData(PurchaseOrderStatus.Draft)]
    [InlineData(PurchaseOrderStatus.Pending)]
    [InlineData(PurchaseOrderStatus.OnHold)]
    [InlineData(PurchaseOrderStatus.PartiallyReceived)]
    public async Task A_status_SAP_never_gives_an_order_asks_SAP_nothing(PurchaseOrderStatus status)
    {
        var result = await HandleSapAsync(new GetPurchaseOrdersFromSAPQuery(1, 10, null, null, null, status, IncludeSummary: true), total: 99);

        Assert.Empty(_sapCalls);
        Assert.Empty(result.Orders);
        Assert.Equal(0, result.TotalCount);
        Assert.Equal(0, result.Summary!.Total);
    }

    [Fact]
    public async Task A_page_past_the_end_reads_no_orders()
    {
        var result = await HandleSapAsync(new GetPurchaseOrdersFromSAPQuery(4, 25, null, null, null), total: 75);

        Assert.DoesNotContain(_sapCalls, call => call.Method == nameof(ISAPServiceLayerClient.GetPurchaseOrderPageAsync));
        Assert.Empty(result.Orders);
        Assert.Null(result.Summary);
    }

    /// <summary>Each filter must select exactly the SAP orders the list maps to that status.</summary>
    [Theory]
    [InlineData("bost_Open", "tNO", PurchaseOrderStatus.Approved)]
    [InlineData("bost_Close", "tNO", PurchaseOrderStatus.Received)]
    [InlineData("bost_Close", "tYES", PurchaseOrderStatus.Cancelled)]
    public void Each_SAP_status_filter_matches_the_orders_mapped_to_it(string documentStatus, string cancelled, PurchaseOrderStatus mapped)
    {
        foreach (var status in new[] { PurchaseOrderStatus.Approved, PurchaseOrderStatus.Received, PurchaseOrderStatus.Cancelled })
        {
            Assert.True(PurchaseOrderSapStatus.TryGetFilter(status, out var filter));
            Assert.Equal(status == mapped, Matches(filter!, documentStatus, cancelled));
        }
    }

    [Fact]
    public async Task The_local_list_counts_every_matching_order_by_status()
    {
        foreach (var (number, status) in new[]
        {
            ("PO-1", PurchaseOrderStatus.Draft), ("PO-2", PurchaseOrderStatus.Pending), ("PO-3", PurchaseOrderStatus.Pending),
            ("PO-4", PurchaseOrderStatus.Approved), ("PO-5", PurchaseOrderStatus.PartiallyReceived), ("PO-6", PurchaseOrderStatus.Received),
            ("PO-7", PurchaseOrderStatus.Cancelled)
        })
        {
            _context.PurchaseOrders.Add(new PurchaseOrderEntity
            {
                OrderNumber = number, CardCode = "S001", Status = status,
                OrderDate = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
        }
        await _context.SaveChangesAsync();

        var service = new PurchaseOrderService(_context, NullLogger<PurchaseOrderService>.Instance);
        var page = await service.GetAllAsync(1, 2, includeSummary: true);
        var pendingOnly = await service.GetAllAsync(1, 2, PurchaseOrderStatus.Pending, includeSummary: true);

        Assert.Equal(2, page.Orders.Count);
        Assert.Equal(7, page.Summary!.Total);
        Assert.Equal((1, 2, 2, 1), (page.Summary.Draft, page.Summary.Pending, page.Summary.Approved, page.Summary.Received));
        Assert.Equal((2, 0, 2, 0), (pendingOnly.Summary!.Total, pendingOnly.Summary.Draft, pendingOnly.Summary.Pending, pendingOnly.Summary.Approved));
        Assert.Null((await service.GetAllAsync(1, 2)).Summary);
    }

    private async Task<ShopInventory.DTOs.PurchaseOrderListResponseDto> HandleSapAsync(GetPurchaseOrdersFromSAPQuery query, int total)
    {
        var sap = StubProxy.For<ISAPServiceLayerClient>((method, args) =>
        {
            _sapCalls.Add((method.Name, args!));
            return method.Name switch
            {
                nameof(ISAPServiceLayerClient.CountPurchaseOrdersAsync) => Task.FromResult(args![3] is string filter && query.Status is null ? filter.Length : total),
                nameof(ISAPServiceLayerClient.GetPurchaseOrderPageAsync) => Task.FromResult(
                    Enumerable.Range(1, (int)args![5]!).Select(i => new SAPPurchaseOrder { DocEntry = i, DocNum = i, DocumentStatus = "bost_Open", Cancelled = "tNO" }).ToList()),
                _ => throw new InvalidOperationException($"{method.Name} was not expected")
            };
        });

        var result = await new GetPurchaseOrdersFromSAPHandler(sap, NullLogger<GetPurchaseOrdersFromSAPHandler>.Instance)
            .Handle(query, CancellationToken.None);
        Assert.False(result.IsError);
        return result.Value;
    }

    /// <summary>Evaluates the "A eq 'x' and B eq 'y'" filters PurchaseOrderSapStatus writes.</summary>
    private static bool Matches(string filter, string documentStatus, string cancelled) =>
        filter.Split(" and ").All(clause =>
        {
            var parts = clause.Split(" eq ");
            var value = parts[1].Trim('\'');
            return parts[0] switch
            {
                "DocumentStatus" => documentStatus == value,
                "Cancelled" => cancelled == value,
                _ => throw new InvalidOperationException(clause)
            };
        });
}
