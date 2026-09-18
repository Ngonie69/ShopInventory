using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Features.InventoryTransfers.Queries.GetPagedTransfers;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The paged transfers endpoint used to ask SAP for the warehouse's transfer count on every page,
/// doubling the SAP work of the Web's page-by-page cache sweeps. These pin that the count is reused
/// in the middle of a walk, and that every field a caller reads is still what a fresh count gives.
/// </summary>
public sealed class PagedTransferCountCacheTests : IDisposable
{
    private const int PageSize = 100;

    private readonly ManualClock _clock = new();
    private readonly MemoryCache _cache;
    private int _count;
    private int _countCalls;
    private int _rowsInWarehouse;

    public PagedTransferCountCacheTests()
    {
        _cache = new MemoryCache(new MemoryCacheOptions { Clock = _clock });
    }

    public void Dispose() => _cache.Dispose();

    private sealed class ManualClock : Microsoft.Extensions.Internal.ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 9, 18, 8, 0, 0, TimeSpan.Zero);
    }

    [Fact]
    public async Task Count_is_not_refetched_for_the_middle_pages_of_a_walk()
    {
        SetWarehouse(rows: 350);
        var handler = CreateHandler();

        var pages = new List<InventoryTransferListResponseDto>();
        for (var page = 1; page <= 3; page++)
        {
            pages.Add(await SendAsync(handler, page));
        }

        Assert.Equal(1, _countCalls);
        Assert.All(pages, response =>
        {
            Assert.True(response.HasMore);
            Assert.Equal(350, response.TotalCount);
            Assert.Equal(4, response.TotalPages);
        });
    }

    [Fact]
    public async Task The_last_page_counts_afresh_so_the_end_of_a_walk_is_exact()
    {
        SetWarehouse(rows: 350);
        var handler = CreateHandler();

        for (var page = 1; page <= 3; page++)
        {
            await SendAsync(handler, page);
        }

        var last = await SendAsync(handler, 4);

        Assert.Equal(2, _countCalls);
        Assert.Equal(50, last.Count);
        Assert.False(last.HasMore);
        Assert.Equal(350, last.TotalCount);
    }

    [Fact]
    public async Task A_full_page_reaching_the_stored_count_sees_transfers_posted_since()
    {
        // The stored count says the walk ends at page 2; SAP has gained rows since. Trusting the
        // stored figure here would end the walk early and leave the newest rows out of the cache.
        SetWarehouse(rows: 200);
        var handler = CreateHandler();
        await SendAsync(handler, 1);

        SetWarehouse(rows: 230);
        var second = await SendAsync(handler, 2);

        Assert.Equal(2, _countCalls);
        Assert.True(second.HasMore);
        Assert.Equal(230, second.TotalCount);
    }

    [Fact]
    public async Task Page_one_always_counts_afresh()
    {
        // Page 1 is what a person opening the list sees, and where every walk starts.
        SetWarehouse(rows: 350);
        var handler = CreateHandler();

        await SendAsync(handler, 1);
        SetWarehouse(rows: 360);
        var again = await SendAsync(handler, 1);

        Assert.Equal(2, _countCalls);
        Assert.Equal(360, again.TotalCount);
    }

    [Fact]
    public async Task A_stored_count_belongs_to_its_own_warehouse()
    {
        SetWarehouse(rows: 350);
        var handler = CreateHandler();

        await SendAsync(handler, 1, "KEFSHOP");
        await SendAsync(handler, 2, "MAIN");

        Assert.Equal(2, _countCalls);
    }

    [Fact]
    public async Task A_stored_count_expires()
    {
        SetWarehouse(rows: 350);
        var handler = CreateHandler();
        await SendAsync(handler, 1);

        _clock.UtcNow += GetPagedTransfersHandler.CountLifetime - TimeSpan.FromSeconds(1);
        await SendAsync(handler, 2);
        Assert.Equal(1, _countCalls);

        _clock.UtcNow += TimeSpan.FromSeconds(2);
        await SendAsync(handler, 3);
        Assert.Equal(2, _countCalls);
    }

    private void SetWarehouse(int rows)
    {
        _rowsInWarehouse = rows;
        _count = rows;
    }

    private static async Task<InventoryTransferListResponseDto> SendAsync(
        GetPagedTransfersHandler handler,
        int page,
        string warehouse = "KEFSHOP")
    {
        var result = await handler.Handle(new GetPagedTransfersQuery(warehouse, page, PageSize), CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        return result.Value;
    }

    private GetPagedTransfersHandler CreateHandler()
    {
        var sap = StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetPagedInventoryTransfersToWarehouseAsync) =>
                Task.FromResult(Page((int)args![1]!, (int)args[2]!)),
            nameof(ISAPServiceLayerClient.GetInventoryTransfersCountAsync) => CountAsync(),
            _ => null
        });

        return new GetPagedTransfersHandler(
            sap,
            Options.Create(new SAPSettings { Enabled = true }),
            _cache,
            NullLogger<GetPagedTransfersHandler>.Instance);
    }

    private Task<int> CountAsync()
    {
        _countCalls++;
        return Task.FromResult(_count);
    }

    private List<InventoryTransfer> Page(int page, int pageSize)
    {
        var skip = (page - 1) * pageSize;
        var rows = Math.Max(0, Math.Min(pageSize, _rowsInWarehouse - skip));

        return Enumerable.Range(skip + 1, rows)
            .Select(docEntry => new InventoryTransfer { DocEntry = docEntry, DocNum = docEntry })
            .ToList();
    }
}
