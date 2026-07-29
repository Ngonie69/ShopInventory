using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Caching;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

[Collection("SapServiceLayerClient")]
public sealed class InventoryTransferPostingPerformanceTests
{
    [Fact]
    public async Task Transfer_item_management_flags_are_loaded_in_one_bulk_request()
    {
        var sap = new TransferServiceLayer();
        var client = CreateClient(sap);

        var result = await client.CreateInventoryTransferAsync(new CreateInventoryTransferRequest
        {
            FromWarehouse = "WH-1",
            ToWarehouse = "WH-2",
            Lines =
            [
                new() { ItemCode = "ITEM-A", Quantity = 1 },
                new() { ItemCode = "ITEM-B", Quantity = 2, BatchNumbers = [] },
                new() { ItemCode = "ITEM-C", Quantity = 3, SerialNumbers = [] }
            ]
        });

        Assert.Equal(101, result.DocEntry);
        Assert.Equal(1, sap.BulkItemMetadataReads);
        Assert.Equal(0, sap.SingleItemReads);
        Assert.Equal(1, sap.TransferPosts);
    }

    [Fact]
    public async Task Explicit_batch_validation_reads_only_transfer_items()
    {
        var sap = new TransferStockStub();
        await using var context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite("Data Source=:memory:")
                .Options);
        var service = new StockValidationService(
            context,
            sap.AsClient(),
            NullLogger<StockValidationService>.Instance);

        var result = await service.ValidateInventoryTransferStockAsync(new CreateInventoryTransferRequest
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
                    ItemCode = "ITEM-B",
                    Quantity = 1,
                    BatchNumbers = [new() { BatchNumber = "B-B", Quantity = 1 }]
                }
            ]
        });

        Assert.True(result.IsValid);
        Assert.Equal(0, sap.WholeWarehouseBatchReads);
        var read = Assert.Single(sap.ScopedBatchReads);
        Assert.Equal("WH-1", read.Warehouse);
        Assert.Equal(["ITEM-A", "ITEM-B"], read.ItemCodes.Order(StringComparer.OrdinalIgnoreCase));
        Assert.NotNull(result.PreFetchedData);
        Assert.Equal(2, result.PreFetchedData.WarehouseBatches["WH-1"]!.Count);
    }

    private static SAPServiceLayerClient CreateClient(TransferServiceLayer sap)
    {
        var httpClient = new HttpClient(sap)
        {
            BaseAddress = new Uri("https://sap.invalid/b1s/v1/")
        };
        var services = new ServiceCollection().BuildServiceProvider();

        return new SAPServiceLayerClient(
            httpClient,
            new SingleClientFactory(httpClient),
            Options.Create(new SAPSettings { ServiceLayerUrl = "https://sap.invalid/b1s/v1/" }),
            new StubHostEnvironment(),
            NullLogger<SAPServiceLayerClient>.Instance,
            new MemoryCache(new MemoryCacheOptions()),
            new CacheSyncStateRecorder(
                services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<CacheSyncStateRecorder>.Instance),
            StubProxy.Unused<ISapItemUomMappingStore>());
    }

    private sealed class TransferServiceLayer : HttpMessageHandler
    {
        public int BulkItemMetadataReads { get; private set; }
        public int SingleItemReads { get; private set; }
        public int TransferPosts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var target = request.RequestUri!.PathAndQuery;

            if (target.EndsWith("/Login", StringComparison.Ordinal))
            {
                return Task.FromResult(Json("{\"SessionId\":\"test-session\"}"));
            }

            if (target.Contains("/Items?", StringComparison.Ordinal))
            {
                BulkItemMetadataReads++;
                return Task.FromResult(Json("""
                    {"value":[
                      {"ItemCode":"ITEM-A","ManageBatchNumbers":"tNO","ManageSerialNumbers":"tNO"},
                      {"ItemCode":"ITEM-B","ManageBatchNumbers":"tNO","ManageSerialNumbers":"tNO"},
                      {"ItemCode":"ITEM-C","ManageBatchNumbers":"tNO","ManageSerialNumbers":"tNO"}
                    ]}
                    """));
            }

            if (target.Contains("/Items(", StringComparison.Ordinal))
            {
                SingleItemReads++;
                return Task.FromResult(Json("{}"));
            }

            if (target.EndsWith("/StockTransfers", StringComparison.Ordinal) &&
                request.Method == HttpMethod.Post)
            {
                TransferPosts++;
                return Task.FromResult(Json(
                    "{\"DocEntry\":101,\"DocNum\":202,\"StockTransferLines\":[]}",
                    HttpStatusCode.Created));
            }

            throw new InvalidOperationException($"Unexpected SAP request: {request.Method} {target}");
        }

        private static HttpResponseMessage Json(
            string body,
            HttpStatusCode statusCode = HttpStatusCode.OK) =>
            new(statusCode) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class TransferStockStub
    {
        public List<(string Warehouse, string[] ItemCodes)> ScopedBatchReads { get; } = [];
        public int WholeWarehouseBatchReads { get; private set; }

        public ISAPServiceLayerClient AsClient() =>
            StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
            {
                nameof(ISAPServiceLayerClient.GetStockQuantitiesForItemsInWarehouseAsync) =>
                    Task.FromResult(new List<StockQuantityDto>
                    {
                        new() { ItemCode = "ITEM-A", WarehouseCode = "WH-1", InStock = 10 },
                        new() { ItemCode = "ITEM-B", WarehouseCode = "WH-1", InStock = 10 }
                    }),
                nameof(ISAPServiceLayerClient.GetBatchNumbersForItemsInWarehouseAsync) =>
                    ScopedBatchRead(
                        (IEnumerable<string>)args![0]!,
                        (string)args[1]!),
                nameof(ISAPServiceLayerClient.GetAllBatchNumbersInWarehouseAsync) =>
                    WholeWarehouseBatchRead(),
                _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
            });

        private Task<List<BatchNumber>> ScopedBatchRead(
            IEnumerable<string> itemCodes,
            string warehouse)
        {
            var codes = itemCodes.ToArray();
            ScopedBatchReads.Add((warehouse, codes));
            return Task.FromResult(new List<BatchNumber>
            {
                new() { ItemCode = "ITEM-A", BatchNum = "B-A", Quantity = 2, Warehouse = warehouse },
                new() { ItemCode = "ITEM-B", BatchNum = "B-B", Quantity = 1, Warehouse = warehouse }
            });
        }

        private Task<List<BatchNumber>> WholeWarehouseBatchRead()
        {
            WholeWarehouseBatchReads++;
            return Task.FromResult(new List<BatchNumber>());
        }
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "ShopInventory.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
