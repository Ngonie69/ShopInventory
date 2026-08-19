using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// SAP measures a batch against the whole document, so the stock check has to add the lines up
/// before comparing. Checked line by line, a pair that each fitted on its own and together did
/// not passed validation and was rejected by SAP as "Insufficient quantity for item ... with
/// batch ... in warehouse".
/// </summary>
public sealed class InventoryTransferBatchDemandTests
{
    [Fact]
    public async Task Two_lines_naming_one_batch_are_added_up_before_the_warehouse_check()
    {
        var result = await Validate(
            new CreateInventoryTransferRequest
            {
                FromWarehouse = "WH-1",
                ToWarehouse = "WH-2",
                Lines =
                [
                    new()
                    {
                        ItemCode = "ITEM-A",
                        Quantity = 2,
                        BatchNumbers = [new() { BatchNumber = "B-A", Quantity = 2 }]
                    },
                    new()
                    {
                        ItemCode = "ITEM-A",
                        Quantity = 2,
                        BatchNumbers = [new() { BatchNumber = "B-A", Quantity = 2 }]
                    }
                ]
            },
            ("ITEM-A", "B-A", 3m));

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Equal("B-A", error.BatchNumber);
        Assert.Equal(4m, error.RequestedQuantity);
        Assert.Equal(3m, error.AvailableQuantity);
    }

    [Fact]
    public async Task One_line_naming_one_batch_twice_is_added_up_the_same_way()
    {
        var result = await Validate(
            new CreateInventoryTransferRequest
            {
                FromWarehouse = "WH-1",
                ToWarehouse = "WH-2",
                Lines =
                [
                    new()
                    {
                        ItemCode = "ITEM-A",
                        Quantity = 4,
                        BatchNumbers =
                        [
                            new() { BatchNumber = "B-A", Quantity = 2 },
                            new() { BatchNumber = "B-A", Quantity = 2 }
                        ]
                    }
                ]
            },
            ("ITEM-A", "B-A", 3m));

        Assert.False(result.IsValid);
        Assert.Equal(4m, Assert.Single(result.Errors).RequestedQuantity);
    }

    [Fact]
    public async Task A_shortfall_reports_what_the_warehouse_actually_holds()
    {
        // The shortage used to read as the whole requested quantity, because the available side
        // was hardcoded to zero however much the batch held.
        var result = await Validate(
            new CreateInventoryTransferRequest
            {
                FromWarehouse = "WH-1",
                ToWarehouse = "WH-2",
                Lines =
                [
                    new()
                    {
                        ItemCode = "ITEM-A",
                        Quantity = 5,
                        BatchNumbers = [new() { BatchNumber = "B-A", Quantity = 5 }]
                    }
                ]
            },
            ("ITEM-A", "B-A", 3m));

        var error = Assert.Single(result.Errors);
        Assert.Equal(3m, error.AvailableQuantity);
        Assert.Equal(2m, error.Shortage);
    }

    [Fact]
    public async Task A_selection_the_warehouse_covers_is_valid()
    {
        var result = await Validate(
            new CreateInventoryTransferRequest
            {
                FromWarehouse = "WH-1",
                ToWarehouse = "WH-2",
                Lines =
                [
                    new()
                    {
                        ItemCode = "ITEM-A",
                        Quantity = 3,
                        BatchNumbers = [new() { BatchNumber = "B-A", Quantity = 3 }]
                    }
                ]
            },
            ("ITEM-A", "B-A", 3m));

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task The_prefetch_says_which_items_its_batch_list_answers_for()
    {
        var result = await Validate(
            new CreateInventoryTransferRequest
            {
                FromWarehouse = "WH-1",
                ToWarehouse = "WH-2",
                Lines =
                [
                    new()
                    {
                        ItemCode = "ITEM-A",
                        Quantity = 1,
                        BatchNumbers = [new() { BatchNumber = "B-A", Quantity = 1 }]
                    },
                    // Leaves its allocation to the poster, so the batch read never covered it.
                    new() { ItemCode = "ITEM-B", Quantity = 1 }
                ]
            },
            ("ITEM-A", "B-A", 5m));

        var prefetched = Assert.IsType<TransferPreFetchedData>(result.PreFetchedData);
        Assert.True(prefetched.CoversBatchesFor("WH-1", "ITEM-A"));
        Assert.False(prefetched.CoversBatchesFor("WH-1", "ITEM-B"));
        Assert.False(prefetched.CoversBatchesFor("WH-9", "ITEM-A"));
    }

    private static async Task<StockValidationResult> Validate(
        CreateInventoryTransferRequest request,
        params (string ItemCode, string BatchNumber, decimal Quantity)[] warehouseBatches)
    {
        await using var context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite("Data Source=:memory:")
                .Options);

        var service = new StockValidationService(
            context,
            SapAnswering(warehouseBatches),
            NullLogger<StockValidationService>.Instance);

        return await service.ValidateInventoryTransferStockAsync(request, CancellationToken.None);
    }

    private static ISAPServiceLayerClient SapAnswering(
        (string ItemCode, string BatchNumber, decimal Quantity)[] warehouseBatches) =>
        StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetStockQuantitiesForItemsInWarehouseAsync) =>
                Task.FromResult(warehouseBatches
                    .GroupBy(batch => batch.ItemCode, StringComparer.OrdinalIgnoreCase)
                    .Select(group => new StockQuantityDto
                    {
                        ItemCode = group.Key,
                        WarehouseCode = (string)args![0]!,
                        InStock = group.Sum(batch => batch.Quantity)
                    })
                    .ToList()),
            nameof(ISAPServiceLayerClient.GetBatchNumbersForItemsInWarehouseAsync) =>
                Task.FromResult(warehouseBatches
                    .Select(batch => new BatchNumber
                    {
                        ItemCode = batch.ItemCode,
                        BatchNum = batch.BatchNumber,
                        Quantity = batch.Quantity,
                        Warehouse = (string)args![1]!
                    })
                    .ToList()),
            _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
        });
}
