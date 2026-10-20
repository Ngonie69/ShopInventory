using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Stock;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Features.InventoryTransfers.Queries.GetTransferRequestStock;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The stock figures shown beside a transfer request before it is converted: what the source
/// warehouse can give of each item, and which lines ask for more than that.
/// </summary>
public sealed class TransferRequestStockTests
{
    private const string Depot = "KEFGRC";
    private const string Van = "VAN002";

    [Fact]
    public async Task Each_line_is_measured_against_what_the_depot_can_give()
    {
        var sap = new StockSap { Stock = { [(Depot, "YOG145")] = 685m, [(Depot, "YOG143")] = 40m } };

        var result = await Handler(sap).Handle(
            new GetTransferRequestStockQuery(7001), default);

        Assert.False(result.IsError);
        Assert.True(result.Value.StockWasFullyRead);
        var yog145 = Assert.Single(result.Value.Lines, line => line.ItemCode == "YOG145");
        Assert.Equal(685m, yog145.Available);
        Assert.Equal(TransferRequestStockLineStates.InStock, yog145.State);

        var yog143 = Assert.Single(result.Value.Lines, line => line.ItemCode == "YOG143");
        Assert.Equal(40m, yog143.Available);
        Assert.Equal(TransferRequestStockLineStates.Short, yog143.State);
        Assert.Equal(1, result.Value.LinesShort);
    }

    [Fact]
    public async Task Units_a_till_has_sold_are_not_available()
    {
        // SAP still counts them, but the conversion will not take them: the sale needs them to post.
        var sap = new StockSap { Stock = { [(Depot, "YOG145")] = 685m, [(Depot, "YOG143")] = 500m } };

        var result = await Handler(sap, held: new Dictionary<string, decimal> { ["YOG145"] = 8m }).Handle(
            new GetTransferRequestStockQuery(7001), default);

        var item = Assert.Single(result.Value.Items, item => item.ItemCode == "YOG145");
        Assert.Equal(685m, item.InStock);
        Assert.Equal(8m, item.HeldByTills);
        Assert.Equal(677m, item.Available);
        Assert.Equal(TransferRequestStockLineStates.Short,
            Assert.Single(result.Value.Lines, line => line.ItemCode == "YOG145").State);
    }

    [Fact]
    public async Task An_item_the_depot_has_no_row_for_has_none()
    {
        var sap = new StockSap { Stock = { [(Depot, "YOG145")] = 685m } };

        var result = await Handler(sap).Handle(new GetTransferRequestStockQuery(7001), default);

        var line = Assert.Single(result.Value.Lines, line => line.ItemCode == "YOG143");
        Assert.Equal(0m, line.Available);
        Assert.Equal(TransferRequestStockLineStates.Short, line.State);
    }

    [Fact]
    public async Task Two_lines_of_one_item_are_measured_together()
    {
        var sap = new StockSap
        {
            Stock = { [(Depot, "YOG145")] = 700m, [(Depot, "YOG143")] = 500m },
            Lines = [("YOG145", 400m), ("YOG145", 400m)]
        };

        var result = await Handler(sap).Handle(new GetTransferRequestStockQuery(7001), default);

        Assert.All(result.Value.Lines, line => Assert.Equal(TransferRequestStockLineStates.Short, line.State));
    }

    [Fact]
    public async Task A_warehouse_that_does_not_answer_is_unread_not_empty()
    {
        var sap = new StockSap { StockReadThrows = true };

        var result = await Handler(sap).Handle(new GetTransferRequestStockQuery(7001), default);

        Assert.False(result.IsError);
        Assert.False(result.Value.StockWasFullyRead);
        Assert.Equal([Depot], result.Value.UnreadableWarehouses);
        Assert.All(result.Value.Lines, line =>
        {
            Assert.Null(line.Available);
            Assert.Equal(TransferRequestStockLineStates.Unread, line.State);
        });
        Assert.Equal(0, result.Value.LinesShort);
    }

    [Fact]
    public async Task An_edit_reads_another_source_and_the_items_it_adds()
    {
        var sap = new StockSap
        {
            Stock = { [("CORMACH", "YOG145")] = 10m, [("CORMACH", "YOG143")] = 900m, [("CORMACH", "MILK1")] = 12m }
        };

        var result = await Handler(sap).Handle(
            new GetTransferRequestStockQuery(7001, FromWarehouse: "CORMACH", ExtraItemCodes: ["MILK1"]), default);

        Assert.Equal(["CORMACH"], sap.WarehousesRead);
        Assert.All(result.Value.Lines, line => Assert.Equal("CORMACH", line.WarehouseCode));
        Assert.Equal(12m, Assert.Single(result.Value.Items, item => item.ItemCode == "MILK1").Available);
        // An added item is not a request line, so it is reported but never counted as short.
        Assert.DoesNotContain(result.Value.Lines, line => line.ItemCode == "MILK1");
    }

    [Fact]
    public async Task A_request_sap_does_not_have_is_not_found()
    {
        var sap = new StockSap { RequestExists = false };

        var result = await Handler(sap).Handle(new GetTransferRequestStockQuery(7001), default);

        Assert.True(result.IsError);
        Assert.Equal(ErrorOr.ErrorType.NotFound, result.FirstError.Type);
    }

    private static GetTransferRequestStockHandler Handler(
        StockSap sap,
        IReadOnlyDictionary<string, decimal>? held = null) =>
        new(
            sap.AsClient(),
            new FixedTillClaims(held ?? new Dictionary<string, decimal>()),
            Options.Create(new SAPSettings { Enabled = true }),
            NullLogger<GetTransferRequestStockHandler>.Instance);

    private sealed class FixedTillClaims(IReadOnlyDictionary<string, decimal> held) : IUnpostedTillClaims
    {
        public Task<IReadOnlyDictionary<string, decimal>> ForWarehouseAsync(
            string warehouseCode, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, decimal>>(
                new Dictionary<string, decimal>(held, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>VAN002's request on KEFGRC: 685 YOG145 and 100 YOG143, unless the test says otherwise.</summary>
    private sealed class StockSap
    {
        public Dictionary<(string Warehouse, string ItemCode), decimal> Stock { get; } = [];
        public List<(string ItemCode, decimal Quantity)> Lines { get; init; } = [("YOG145", 685m), ("YOG143", 100m)];
        public bool StockReadThrows { get; init; }
        public bool RequestExists { get; init; } = true;
        public List<string> WarehousesRead { get; } = [];

        public ISAPServiceLayerClient AsClient() =>
            StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
            {
                nameof(ISAPServiceLayerClient.GetInventoryTransferRequestByDocEntryAsync) =>
                    Task.FromResult(RequestExists ? Request((int)args![0]!) : null),
                nameof(ISAPServiceLayerClient.GetStockQuantitiesForItemsInWarehouseAsync) =>
                    ReadStock((string)args![0]!, (IEnumerable<string>)args[1]!),
                _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
            });

        private Task<List<StockQuantityDto>> ReadStock(string warehouse, IEnumerable<string> itemCodes)
        {
            WarehousesRead.Add(warehouse);
            if (StockReadThrows)
                throw new HttpRequestException("Service Layer did not answer");

            return Task.FromResult(itemCodes
                .Where(code => Stock.ContainsKey((warehouse, code)))
                .Select(code => new StockQuantityDto
                {
                    ItemCode = code,
                    WarehouseCode = warehouse,
                    InStock = Stock[(warehouse, code)]
                })
                .ToList());
        }

        private InventoryTransferRequest? Request(int docEntry) => new()
        {
            DocEntry = docEntry,
            DocNum = 89450,
            FromWarehouse = Depot,
            ToWarehouse = Van,
            DocumentStatus = "bost_Open",
            StockTransferLines = Lines
                .Select((line, index) => new InventoryTransferRequestLine
                {
                    LineNum = index,
                    ItemCode = line.ItemCode,
                    Quantity = line.Quantity,
                    FromWarehouseCode = Depot,
                    WarehouseCode = Van
                })
                .ToList()
        };
    }
}
