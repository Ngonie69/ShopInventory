using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
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
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Serial-managed items on sales documents, and the management flags the allocation depends on.
/// Both used to end at SAP's -4014: an invoice had nowhere to put serial numbers, and the flag
/// that decides whether batches are allocated was read from a column nothing ever writes.
/// </summary>
[Collection("SapServiceLayerClient")]
public sealed class InvoiceSerialAndBatchFlagTests
{
    [Fact]
    public async Task Invoice_carries_the_serial_numbers_of_a_serial_managed_line()
    {
        var sap = new DocumentServiceLayer();
        var client = CreateClient(sap);

        await client.CreateInvoiceAsync(new CreateInvoiceRequest
        {
            CardCode = "C-1",
            DocCurrency = "USD",
            Lines =
            [
                new()
                {
                    ItemCode = "ITEM-S",
                    Quantity = 2,
                    WarehouseCode = "WH-1",
                    SerialNumbers =
                    [
                        new() { InternalSerialNumber = "S-1", SystemSerialNumber = 11 },
                        new() { InternalSerialNumber = "S-2" }
                    ]
                }
            ]
        });

        var serials = sap.PostedSerialNumbers("Invoices", lineIndex: 0);
        Assert.Equal(["S-1", "S-2"], serials);
    }

    [Fact]
    public async Task Invoice_with_fewer_serial_numbers_than_units_is_rejected()
    {
        var sap = new DocumentServiceLayer();
        var client = CreateClient(sap);

        var failure = await Assert.ThrowsAsync<ArgumentException>(() =>
            client.CreateInvoiceAsync(new CreateInvoiceRequest
            {
                CardCode = "C-1",
                DocCurrency = "USD",
                Lines =
                [
                    new()
                    {
                        ItemCode = "ITEM-S",
                        Quantity = 3,
                        WarehouseCode = "WH-1",
                        SerialNumbers = [new() { InternalSerialNumber = "S-1" }]
                    }
                ]
            }));

        Assert.Contains("covers 1 of 3 units", failure.Message);
        Assert.Equal(0, sap.Posts);
    }

    [Fact]
    public async Task Invoice_batch_selection_short_of_the_line_quantity_is_rejected()
    {
        var sap = new DocumentServiceLayer();
        var client = CreateClient(sap);

        var failure = await Assert.ThrowsAsync<ArgumentException>(() =>
            client.CreateInvoiceAsync(new CreateInvoiceRequest
            {
                CardCode = "C-1",
                DocCurrency = "USD",
                Lines =
                [
                    new()
                    {
                        ItemCode = "ITEM-A",
                        Quantity = 5,
                        WarehouseCode = "WH-1",
                        BatchNumbers = [new() { BatchNumber = "B-1", Quantity = 2 }]
                    }
                ]
            }));

        Assert.Contains("covers 2 of 5", failure.Message);
        Assert.Equal(0, sap.Posts);
    }

    [Fact]
    public async Task Credit_note_carries_the_serial_numbers_of_a_returned_line()
    {
        var sap = new DocumentServiceLayer();
        var client = CreateClient(sap);

        await client.CreateCreditNoteAsync(new CreateCreditNoteRequest
        {
            CardCode = "C-1",
            Lines =
            [
                new()
                {
                    ItemCode = "ITEM-S",
                    Quantity = 2,
                    WarehouseCode = "WH-1",
                    SerialNumbers =
                    [
                        new() { InternalSerialNumber = "S-9" },
                        new() { InternalSerialNumber = "S-8" }
                    ]
                }
            ]
        });

        Assert.Equal(["S-9", "S-8"], sap.PostedSerialNumbers("CreditNotes", lineIndex: 0));
    }

    [Fact]
    public async Task Batch_management_is_read_from_sap_not_from_the_local_product_row()
    {
        // The local row exists with the flag false — its default, since nothing writes it. Trusting
        // it meant the batches were never allocated and SAP rejected the invoice.
        await using var context = InMemoryContext();
        context.Products.Add(new ProductEntity
        {
            ItemCode = "ITEM-A",
            ItemName = "Batch-managed item",
            ManageBatchNumbers = false
        });
        await context.SaveChangesAsync();

        var service = CreateValidationService(context, sapItem: new Item
        {
            ItemCode = "ITEM-A",
            ManageBatchNumbers = "tYES",
            ManageSerialNumbers = "tNO"
        });

        Assert.True(await service.IsBatchManagedItemAsync("ITEM-A"));
    }

    [Fact]
    public async Task Unreadable_management_flags_stop_the_document_instead_of_defaulting()
    {
        await using var context = InMemoryContext();
        var service = CreateValidationService(context, sapItem: null);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.IsBatchManagedItemAsync("ITEM-A"));

        Assert.Contains("cannot be determined", failure.Message);
    }

    private static ApplicationDbContext InMemoryContext()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connection)
                .Options);
        context.Database.EnsureCreated();
        return context;
    }

    private static BatchInventoryValidationService CreateValidationService(
        ApplicationDbContext context,
        Item? sapItem) =>
        new(
            context,
            StubProxy.For<ISAPServiceLayerClient>((method, _) => method.Name switch
            {
                nameof(ISAPServiceLayerClient.GetItemByCodeAsync) => Task.FromResult(sapItem),
                _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
            }),
            StubProxy.Unused<IInventoryLockService>(),
            NullLogger<BatchInventoryValidationService>.Instance);

    private static SAPServiceLayerClient CreateClient(DocumentServiceLayer sap)
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

    private sealed class DocumentServiceLayer : HttpMessageHandler
    {
        public int Posts { get; private set; }
        private readonly Dictionary<string, string> _posted = new(StringComparer.Ordinal);

        /// <summary>The serial numbers posted on one line of the named document, in payload order.</summary>
        public List<string> PostedSerialNumbers(string resource, int lineIndex)
        {
            Assert.True(_posted.ContainsKey(resource), $"No {resource} document was posted");
            using var document = JsonDocument.Parse(_posted[resource]);
            var line = document.RootElement.GetProperty("DocumentLines")[lineIndex];
            return line.GetProperty("SerialNumbers")
                .EnumerateArray()
                .Select(serial => serial.GetProperty("InternalSerialNumber").GetString()!)
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

            foreach (var resource in new[] { "Invoices", "CreditNotes" })
            {
                if (target.EndsWith($"/{resource}", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
                {
                    Posts++;
                    _posted[resource] = await request.Content!.ReadAsStringAsync(cancellationToken);
                    return Json("{\"DocEntry\":501,\"DocNum\":601,\"DocumentLines\":[]}", HttpStatusCode.Created);
                }
            }

            throw new InvalidOperationException($"Unexpected SAP request: {request.Method} {target}");
        }

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
