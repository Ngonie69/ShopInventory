using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Caching;
using ShopInventory.Configuration;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// SAP answers an incomplete batch/serial selection with -4014 ("Cannot add row without complete
/// selection of batch/serial numbers") and names neither the row nor the item. These cover the
/// cases that used to reach SAP in that state.
/// </summary>
[Collection("SapServiceLayerClient")]
public sealed class InventoryTransferBatchSerialAllocationTests
{
    [Fact]
    public async Task Batch_managed_line_is_allocated_across_batches_up_to_the_line_quantity()
    {
        var sap = new AllocationServiceLayer
        {
            Items =
            {
                ["ITEM-A"] = (Batch: true, Serial: false)
            }
        };
        var client = CreateClient(sap);

        await client.CreateInventoryTransferAsync(
            new CreateInventoryTransferRequest
            {
                FromWarehouse = "WH-1",
                ToWarehouse = "WH-2",
                Lines = [new() { ItemCode = "ITEM-A", Quantity = 4 }]
            },
            PreFetched(("ITEM-A", "B-1", 3m), ("ITEM-A", "B-2", 5m)),
            CancellationToken.None);

        var allocations = sap.PostedBatchAllocations(lineIndex: 0);
        Assert.Equal(new[] { ("B-1", 3m), ("B-2", 1m) }, allocations);
    }

    [Fact]
    public async Task Two_lines_for_the_same_item_do_not_allocate_the_same_batch_twice()
    {
        var sap = new AllocationServiceLayer
        {
            Items =
            {
                ["ITEM-A"] = (Batch: true, Serial: false)
            }
        };
        var client = CreateClient(sap);

        await client.CreateInventoryTransferAsync(
            new CreateInventoryTransferRequest
            {
                FromWarehouse = "WH-1",
                ToWarehouse = "WH-2",
                Lines =
                [
                    new() { ItemCode = "ITEM-A", Quantity = 2 },
                    new() { ItemCode = "ITEM-A", Quantity = 2 }
                ]
            },
            PreFetched(("ITEM-A", "B-1", 3m), ("ITEM-A", "B-2", 2m)),
            CancellationToken.None);

        Assert.Equal(new[] { ("B-1", 2m) }, sap.PostedBatchAllocations(lineIndex: 0));
        Assert.Equal(new[] { ("B-1", 1m), ("B-2", 1m) }, sap.PostedBatchAllocations(lineIndex: 1));
    }

    [Fact]
    public async Task Second_line_reports_the_shortfall_left_by_the_first_instead_of_posting()
    {
        var sap = new AllocationServiceLayer
        {
            Items =
            {
                ["ITEM-A"] = (Batch: true, Serial: false)
            }
        };
        var client = CreateClient(sap);

        var failure = await Assert.ThrowsAsync<ArgumentException>(() =>
            client.CreateInventoryTransferAsync(
                new CreateInventoryTransferRequest
                {
                    FromWarehouse = "WH-1",
                    ToWarehouse = "WH-2",
                    Lines =
                    [
                        new() { ItemCode = "ITEM-A", Quantity = 2 },
                        new() { ItemCode = "ITEM-A", Quantity = 2 }
                    ]
                },
                PreFetched(("ITEM-A", "B-1", 3m)),
                CancellationToken.None));

        Assert.Contains("Insufficient batch quantity", failure.Message);
        Assert.Equal(0, sap.TransferPosts);
    }

    [Fact]
    public async Task Explicit_batch_selection_short_of_the_line_quantity_is_rejected()
    {
        var sap = new AllocationServiceLayer
        {
            Items =
            {
                ["ITEM-A"] = (Batch: true, Serial: false)
            }
        };
        var client = CreateClient(sap);

        var failure = await Assert.ThrowsAsync<ArgumentException>(() =>
            client.CreateInventoryTransferAsync(new CreateInventoryTransferRequest
            {
                FromWarehouse = "WH-1",
                ToWarehouse = "WH-2",
                Lines =
                [
                    new()
                    {
                        ItemCode = "ITEM-A",
                        Quantity = 5,
                        BatchNumbers = [new() { BatchNumber = "B-1", Quantity = 3 }]
                    }
                ]
            }));

        Assert.Contains("covers 3 of 5", failure.Message);
        Assert.Equal(0, sap.TransferPosts);
    }

    [Fact]
    public async Task Explicit_serial_selection_short_of_the_line_quantity_is_rejected()
    {
        var sap = new AllocationServiceLayer
        {
            Items =
            {
                ["ITEM-S"] = (Batch: false, Serial: true)
            }
        };
        var client = CreateClient(sap);

        var failure = await Assert.ThrowsAsync<ArgumentException>(() =>
            client.CreateInventoryTransferAsync(new CreateInventoryTransferRequest
            {
                FromWarehouse = "WH-1",
                ToWarehouse = "WH-2",
                Lines =
                [
                    new()
                    {
                        ItemCode = "ITEM-S",
                        Quantity = 3,
                        SerialNumbers = [new() { InternalSerialNumber = "S-1" }]
                    }
                ]
            }));

        Assert.Contains("covers 1 of 3", failure.Message);
        Assert.Equal(0, sap.TransferPosts);
    }

    [Fact]
    public async Task Serial_managed_line_cannot_carry_a_fractional_quantity()
    {
        var sap = new AllocationServiceLayer
        {
            Items =
            {
                ["ITEM-S"] = (Batch: false, Serial: true)
            }
        };
        var client = CreateClient(sap);

        var failure = await Assert.ThrowsAsync<ArgumentException>(() =>
            client.CreateInventoryTransferAsync(new CreateInventoryTransferRequest
            {
                FromWarehouse = "WH-1",
                ToWarehouse = "WH-2",
                Lines = [new() { ItemCode = "ITEM-S", Quantity = 2.5m }]
            }));

        Assert.Contains("whole number of units", failure.Message);
        Assert.Equal(0, sap.TransferPosts);
    }

    [Fact]
    public async Task Item_missing_from_the_bulk_metadata_read_is_resolved_before_posting()
    {
        // The bulk read seeds an entry for every requested item; leaving it unresolved used to be
        // read as "not batch-managed" and the line was posted without any allocation.
        var sap = new AllocationServiceLayer
        {
            OmitFromBulkMetadata = { "ITEM-A" },
            Items =
            {
                ["ITEM-A"] = (Batch: true, Serial: false)
            }
        };
        var client = CreateClient(sap);

        var failure = await Assert.ThrowsAsync<ArgumentException>(() =>
            client.CreateInventoryTransferAsync(new CreateInventoryTransferRequest
            {
                FromWarehouse = "WH-1",
                ToWarehouse = "WH-2",
                Lines = [new() { ItemCode = "ITEM-A", Quantity = 1 }]
            }));

        Assert.Equal(1, sap.SingleItemReads);
        Assert.Contains("No batches found", failure.Message);
        Assert.Equal(0, sap.TransferPosts);
    }

    [Fact]
    public async Task Sap_rejection_for_an_incomplete_selection_is_reported_in_plain_words()
    {
        var sap = new AllocationServiceLayer
        {
            Items = { ["ITEM-A"] = (Batch: false, Serial: false) },
            TransferError = (HttpStatusCode.BadRequest,
                """{"error":{"code":-4014,"message":{"lang":"en-us","value":"Cannot add row without complete selection of batch/serial numbers"}}}""")
        };
        var client = CreateClient(sap);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CreateInventoryTransferAsync(new CreateInventoryTransferRequest
            {
                FromWarehouse = "WH-1",
                ToWarehouse = "WH-2",
                Lines = [new() { ItemCode = "ITEM-A", Quantity = 1 }]
            }));

        Assert.Contains("incomplete batch/serial selection", failure.Message);
    }

    [Fact]
    public async Task Converting_a_request_allocates_its_lines_against_one_shared_pool()
    {
        // Converting a transfer request used to allocate the lines itself, giving each line the
        // full batch pool. It now defers to the same allocation the posted document goes through.
        var sap = new AllocationServiceLayer
        {
            Items = { ["ITEM-A"] = (Batch: true, Serial: false) },
            WarehouseBatches = { ("ITEM-A", "B-1", 3m), ("ITEM-A", "B-2", 2m) },
            TransferRequest = """
                {"DocEntry":55,"DocNum":900,"FromWarehouse":"WH-1","ToWarehouse":"WH-2","DocumentStatus":"bost_Open",
                 "StockTransferLines":[
                   {"LineNum":0,"ItemCode":"ITEM-A","Quantity":2,"FromWarehouseCode":"WH-1","WarehouseCode":"WH-2"},
                   {"LineNum":1,"ItemCode":"ITEM-A","Quantity":2,"FromWarehouseCode":"WH-1","WarehouseCode":"WH-2"}]}
                """
        };
        var client = CreateClient(sap);

        await client.ConvertTransferRequestToTransferAsync(55, CancellationToken.None);

        Assert.Equal(new[] { ("B-1", 2m) }, sap.PostedBatchAllocations(lineIndex: 0));
        Assert.Equal(new[] { ("B-1", 1m), ("B-2", 1m) }, sap.PostedBatchAllocations(lineIndex: 1));
    }

    [Fact]
    public async Task Explicit_batch_selection_beyond_the_warehouse_quantity_is_rejected()
    {
        var sap = new AllocationServiceLayer
        {
            Items =
            {
                ["ITEM-A"] = (Batch: true, Serial: false)
            }
        };
        var client = CreateClient(sap);

        var failure = await Assert.ThrowsAsync<ArgumentException>(() =>
            client.CreateInventoryTransferAsync(
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
                            BatchNumbers = [new() { BatchNumber = "B-1", Quantity = 5 }]
                        }
                    ]
                },
                PreFetched(("ITEM-A", "B-1", 2m)),
                CancellationToken.None));

        Assert.Contains("has 2 left in warehouse WH-1", failure.Message);
        Assert.Equal(0, sap.TransferPosts);
    }

    [Fact]
    public async Task Two_lines_selecting_the_same_batch_are_measured_against_one_pool()
    {
        var sap = new AllocationServiceLayer
        {
            Items =
            {
                ["ITEM-A"] = (Batch: true, Serial: false)
            }
        };
        var client = CreateClient(sap);

        var failure = await Assert.ThrowsAsync<ArgumentException>(() =>
            client.CreateInventoryTransferAsync(
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
                            BatchNumbers = [new() { BatchNumber = "B-1", Quantity = 2 }]
                        },
                        new()
                        {
                            ItemCode = "ITEM-A",
                            Quantity = 2,
                            BatchNumbers = [new() { BatchNumber = "B-1", Quantity = 2 }]
                        }
                    ]
                },
                PreFetched(("ITEM-A", "B-1", 3m)),
                CancellationToken.None));

        Assert.Contains("Line 2", failure.Message);
        Assert.Contains("has 1 left in warehouse WH-1", failure.Message);
        Assert.Equal(0, sap.TransferPosts);
    }

    [Fact]
    public async Task Explicit_selection_is_read_from_the_warehouse_when_nothing_was_prefetched()
    {
        var sap = new AllocationServiceLayer
        {
            Items = { ["ITEM-A"] = (Batch: true, Serial: false) },
            WarehouseBatches = { ("ITEM-A", "B-1", 1m) }
        };
        var client = CreateClient(sap);

        var failure = await Assert.ThrowsAsync<ArgumentException>(() =>
            client.CreateInventoryTransferAsync(new CreateInventoryTransferRequest
            {
                FromWarehouse = "WH-1",
                ToWarehouse = "WH-2",
                Lines =
                [
                    new()
                    {
                        ItemCode = "ITEM-A",
                        Quantity = 3,
                        BatchNumbers = [new() { BatchNumber = "B-1", Quantity = 3 }]
                    }
                ]
            }));

        Assert.Contains("has 1 left in warehouse WH-1", failure.Message);
        Assert.Equal(0, sap.TransferPosts);
    }

    [Fact]
    public async Task Explicit_selection_within_the_warehouse_quantity_posts_unchanged()
    {
        var sap = new AllocationServiceLayer
        {
            Items =
            {
                ["ITEM-A"] = (Batch: true, Serial: false)
            }
        };
        var client = CreateClient(sap);

        await client.CreateInventoryTransferAsync(
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
                        BatchNumbers =
                        [
                            new() { BatchNumber = "B-1", Quantity = 2 },
                            new() { BatchNumber = "B-2", Quantity = 1 }
                        ]
                    }
                ]
            },
            PreFetched(("ITEM-A", "B-1", 2m), ("ITEM-A", "B-2", 4m)),
            CancellationToken.None);

        Assert.Equal(new[] { ("B-1", 2m), ("B-2", 1m) }, sap.PostedBatchAllocations(lineIndex: 0));
    }

    [Fact]
    public async Task A_chosen_line_and_an_auto_allocated_line_are_both_covered_in_one_document()
    {
        var sap = new AllocationServiceLayer
        {
            Items =
            {
                ["ITEM-A"] = (Batch: true, Serial: false),
                ["ITEM-B"] = (Batch: true, Serial: false)
            }
        };
        var client = CreateClient(sap);

        await client.CreateInventoryTransferAsync(
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
                        BatchNumbers = [new() { BatchNumber = "B-1", Quantity = 2 }]
                    },
                    new() { ItemCode = "ITEM-B", Quantity = 2 }
                ]
            },
            PreFetched(("ITEM-A", "B-1", 5m), ("ITEM-B", "B-2", 4m)),
            CancellationToken.None);

        Assert.Equal(new[] { ("B-1", 2m) }, sap.PostedBatchAllocations(lineIndex: 0));
        Assert.Equal(new[] { ("B-2", 2m) }, sap.PostedBatchAllocations(lineIndex: 1));
    }

    [Fact]
    public async Task A_stock_rejection_from_sap_is_reported_in_plain_words()
    {
        var sap = new AllocationServiceLayer
        {
            Items = { ["ITEM-A"] = (Batch: false, Serial: false) },
            TransferError = (HttpStatusCode.BadRequest,
                """{"error":{"code":-10,"message":{"lang":"en-us","value":"10001153 - Insufficient quantity for item YOG144 with batch YOG144/H06/26 in warehouse"}}}""")
        };
        var client = CreateClient(sap);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CreateInventoryTransferAsync(new CreateInventoryTransferRequest
            {
                FromWarehouse = "WH-1",
                ToWarehouse = "WH-2",
                Lines = [new() { ItemCode = "ITEM-A", Quantity = 1 }]
            }));

        Assert.Contains("not enough stock in the source warehouse", failure.Message);
        Assert.Contains("YOG144/H06/26", failure.Message);
        // The raw envelope stays on the log line; a person is handed the sentence inside it.
        Assert.DoesNotContain("\"error\"", failure.Message);
        // And the wording still has to read as a rejection no retry can clear, which is what
        // sends a queued transfer to review instead of round the queue again.
        Assert.True(SapFailureClassifier.IsPermanentStockRejection(failure.Message));
    }

    [Fact]
    public async Task A_batch_read_that_fails_is_reported_as_a_failure_not_as_an_empty_warehouse()
    {
        var sap = new AllocationServiceLayer
        {
            Items = { ["ITEM-A"] = (Batch: true, Serial: false) },
            BatchReadError = (HttpStatusCode.BadRequest,
                """{"error":{"code":-1,"message":{"lang":"en-us","value":"Internal error (-1) occurred"}}}""")
        };
        var client = CreateClient(sap);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CreateInventoryTransferAsync(new CreateInventoryTransferRequest
            {
                FromWarehouse = "WH-1",
                ToWarehouse = "WH-2",
                Lines = [new() { ItemCode = "ITEM-A", Quantity = 1 }]
            }));

        Assert.Contains("could not read the batches", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(
            SapFailureClassifier.IsPermanentStockRejection(failure.Message),
            "a read that failed is not a shortage, and must stay retryable");
        Assert.Equal(0, sap.TransferPosts);
    }

    private static TransferPreFetchedData PreFetched(params (string ItemCode, string BatchNumber, decimal Quantity)[] batches)
    {
        var prefetched = new TransferPreFetchedData();
        prefetched.WarehouseBatches["WH-1"] = batches
            .Select(b => new BatchNumber
            {
                ItemCode = b.ItemCode,
                BatchNum = b.BatchNumber,
                Quantity = b.Quantity,
                Warehouse = "WH-1"
            })
            .ToList();
        // As validation records it: the list answers for the items it was read for, and for no
        // others, so an item outside it is fetched rather than read as an empty warehouse.
        prefetched.WarehouseBatchItemCodes["WH-1"] = new HashSet<string>(
            batches.Select(b => b.ItemCode),
            StringComparer.OrdinalIgnoreCase);
        return prefetched;
    }

    private static SAPServiceLayerClient CreateClient(AllocationServiceLayer sap)
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

    private sealed class AllocationServiceLayer : HttpMessageHandler
    {
        public Dictionary<string, (bool Batch, bool Serial)> Items { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Items the bulk metadata read leaves out, as SAP does when a read partly fails.</summary>
        public HashSet<string> OmitFromBulkMetadata { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Batch stock the warehouse SQL query reports, for flows with no pre-fetched data.</summary>
        public List<(string ItemCode, string BatchNumber, decimal Quantity)> WarehouseBatches { get; } = [];

        /// <summary>The document GET InventoryTransferRequests(n) answers with.</summary>
        public string? TransferRequest { get; init; }

        public (HttpStatusCode Status, string Body)? TransferError { get; init; }

        /// <summary>Makes the batch stock query fail, as a lost SQLQueries object does.</summary>
        public (HttpStatusCode Status, string Body)? BatchReadError { get; init; }

        public int SingleItemReads { get; private set; }
        public int TransferPosts { get; private set; }
        public string? PostedTransfer { get; private set; }

        /// <summary>The (batch number, quantity) pairs posted for one line, in payload order.</summary>
        public List<(string BatchNumber, decimal Quantity)> PostedBatchAllocations(int lineIndex)
        {
            Assert.NotNull(PostedTransfer);
            using var document = JsonDocument.Parse(PostedTransfer!);
            var line = document.RootElement.GetProperty("StockTransferLines")[lineIndex];
            return line.GetProperty("BatchNumbers")
                .EnumerateArray()
                .Select(batch => (
                    batch.GetProperty("BatchNumber").GetString()!,
                    batch.GetProperty("Quantity").GetDecimal()))
                .ToList();
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var target = request.RequestUri!.PathAndQuery;

            if (target.EndsWith("/Login", StringComparison.Ordinal))
            {
                return Json("{\"SessionId\":\"test-session\"}");
            }

            if (target.Contains("/Items?", StringComparison.Ordinal))
            {
                var resolved = Items
                    .Where(item => !OmitFromBulkMetadata.Contains(item.Key))
                    .Select(item => ItemJson(item.Key, item.Value));
                return Json($"{{\"value\":[{string.Join(",", resolved)}]}}");
            }

            if (target.Contains("/Items('", StringComparison.Ordinal))
            {
                SingleItemReads++;
                var itemCode = target.Split('\'')[1];
                return Items.TryGetValue(itemCode, out var flags)
                    ? Json(ItemJson(itemCode, flags))
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (target.Contains("/InventoryTransferRequests(", StringComparison.Ordinal))
            {
                if (target.EndsWith("/Close", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }

                return TransferRequest is null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : Json(TransferRequest);
            }

            // The batch stock query. Tests that leave WarehouseBatches empty are the ones asserting
            // that a managed line fails locally rather than at SAP.
            if (target.Contains("/SQLQueries", StringComparison.Ordinal))
            {
                if (request.Method == HttpMethod.Post)
                {
                    return Json("{}", HttpStatusCode.Created);
                }

                if (!target.Contains("/List", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                if (BatchReadError is { } batchError)
                {
                    return Json(batchError.Body, batchError.Status);
                }

                // The reader pages until a page comes back short, so only the first page has rows.
                var rows = target.Contains("$skip=0", StringComparison.Ordinal) || !target.Contains("$skip=", StringComparison.Ordinal)
                    ? WarehouseBatches.Select(b =>
                        $$"""{"ItemCode":"{{b.ItemCode}}","BatchNum":"{{b.BatchNumber}}","InStock":{{b.Quantity}},"WhsCode":"WH-1"}""")
                    : [];
                return Json($"{{\"value\":[{string.Join(",", rows)}]}}");
            }

            if (target.EndsWith("/StockTransfers", StringComparison.Ordinal) &&
                request.Method == HttpMethod.Post)
            {
                TransferPosts++;
                PostedTransfer = await request.Content!.ReadAsStringAsync(cancellationToken);

                return TransferError is { } error
                    ? Json(error.Body, error.Status)
                    : Json("{\"DocEntry\":101,\"DocNum\":202,\"StockTransferLines\":[]}", HttpStatusCode.Created);
            }

            throw new InvalidOperationException($"Unexpected SAP request: {request.Method} {target}");
        }

        private static string ItemJson(string itemCode, (bool Batch, bool Serial) flags) =>
            $$"""
              {"ItemCode":"{{itemCode}}","ManageBatchNumbers":"{{(flags.Batch ? "tYES" : "tNO")}}","ManageSerialNumbers":"{{(flags.Serial ? "tYES" : "tNO")}}"}
              """;

        private static HttpResponseMessage Json(
            string body,
            HttpStatusCode statusCode = HttpStatusCode.OK) =>
            new(statusCode) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
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
