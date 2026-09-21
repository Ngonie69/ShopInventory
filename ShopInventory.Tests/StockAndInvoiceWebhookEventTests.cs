using System.Net;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Caching;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Features.CreditNotes.Queries.GetCreditNoteReasons;
using ShopInventory.Features.GoodsReceiptPurchaseOrders.Commands.CreateGoodsReceiptPurchaseOrder;
using ShopInventory.Features.Invoices.Commands.CancelInvoice;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// <c>stock.transfer</c>, <c>invoice.cancelled</c> and <c>inventory.received</c> are published from the
/// sites that genuinely mean them — and only those.
/// </summary>
/// <remarks>
/// The plan named eight sites for these; five were wrong once read. A transfer <em>request</em> moves
/// no stock. The app's own purchase-order Receive updates a local tracker that never reaches SAP. The
/// negative-stock census is a daily count with no item in it, not a stock-out. The tests below pin
/// the three that are right, and <c>stock.transfer</c> is pinned where every transfer passes rather
/// than at the one handler the plan named, which would have covered one path in five.
/// </remarks>
public sealed class StockAndInvoiceWebhookEventTests
{
    [Fact]
    public async Task A_transfer_sap_accepts_publishes_stock_transfer_with_its_warehouses()
    {
        var recorded = new RecordingWebhookService();
        var client = BuildClient(new TransferServiceLayer(), recorded);

        await client.CreateInventoryTransferAsync(
            new CreateInventoryTransferRequest
            {
                FromWarehouse = "WH-1",
                ToWarehouse = "WH-2",
                Lines = [new() { ItemCode = "ITEM-A", Quantity = 4 }]
            },
            CancellationToken.None);

        var published = Assert.Single(recorded.Published);
        Assert.Equal(WebhookEventTypes.StockTransfer, published.EventType);

        var payload = System.Text.Json.JsonSerializer.Serialize(published.Payload);
        Assert.Contains("\"docEntry\":101", payload);
        Assert.Contains("\"docNum\":202", payload);
        Assert.Contains("\"fromWarehouse\":\"WH-1\"", payload);
        Assert.Contains("\"toWarehouse\":\"WH-2\"", payload);
    }

    [Fact]
    public async Task A_transfer_sap_refuses_publishes_nothing()
    {
        var recorded = new RecordingWebhookService();
        var client = BuildClient(
            new TransferServiceLayer
            {
                TransferError = (HttpStatusCode.BadRequest,
                    "{\"error\":{\"code\":-10,\"message\":{\"value\":\"Something SAP disliked\"}}}")
            },
            recorded);

        await Assert.ThrowsAnyAsync<Exception>(() => client.CreateInventoryTransferAsync(
            new CreateInventoryTransferRequest
            {
                FromWarehouse = "WH-1",
                ToWarehouse = "WH-2",
                Lines = [new() { ItemCode = "ITEM-A", Quantity = 4 }]
            },
            CancellationToken.None));

        // Nothing moved, so nothing may be announced.
        Assert.Empty(recorded.Published);
    }

    [Fact]
    public void An_invoice_cancellation_carries_invoice_cancelled_although_its_category_is_credit_note()
    {
        var request = InvoiceCancellationNotificationFactory.Create(
            new Invoice { DocEntry = 9002, DocNum = 4711, CardCode = "C001", CardName = "Acme", DocCurrency = "USD", DocTotal = 120m },
            new CreditNoteDto { Id = 7, CreditNoteNumber = "CN-7", SAPDocNum = 880 },
            new CreditNoteReasonOption("Error", "Raised in error"),
            comments: null);

        Assert.Equal(WebhookEventTypes.InvoiceCancelled, request.WebhookEvent);

        // The reason the event is named rather than read off the category: these disagree.
        Assert.Equal("CreditNote", request.Category);
    }

    [Fact]
    public async Task A_goods_receipt_po_carries_inventory_received()
    {
        CreateNotificationRequest? raised = null;

        var notifications = StubProxy.For<INotificationService>((method, args) =>
        {
            if (method.Name == nameof(INotificationService.CreateNotificationAsync))
            {
                raised = (CreateNotificationRequest)args![0]!;
                return Task.FromResult(new NotificationDto());
            }

            throw new InvalidOperationException($"{method.Name} was not expected.");
        });

        var sap = StubProxy.For<ISAPServiceLayerClient>((method, _) =>
            method.Name == nameof(ISAPServiceLayerClient.CreateGoodsReceiptPurchaseOrderAsync)
                ? Task.FromResult(new SAPGoodsReceiptPurchaseOrder
                {
                    DocEntry = 55,
                    DocNum = 66,
                    CardCode = "S001",
                    CardName = "Supplier",
                    DocCurrency = "USD",
                    DocTotal = 300m
                })
                : throw new InvalidOperationException($"{method.Name} was not expected."));

        var handler = new CreateGoodsReceiptPurchaseOrderHandler(
            sap,
            notifications,
            NullLogger<CreateGoodsReceiptPurchaseOrderHandler>.Instance);

        var result = await handler.Handle(
            new CreateGoodsReceiptPurchaseOrderCommand(new CreateGoodsReceiptPurchaseOrderRequest { CardCode = "S001" }),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.NotNull(raised);
        Assert.Equal(WebhookEventTypes.InventoryReceived, raised!.WebhookEvent);
    }

    private static SAPServiceLayerClient BuildClient(HttpMessageHandler serviceLayer, RecordingWebhookService recorded)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWebhookService>(recorded);
        var provider = services.BuildServiceProvider();

        var httpClient = new HttpClient(serviceLayer) { BaseAddress = new Uri("https://sap.invalid/b1s/v1/") };

        return new SAPServiceLayerClient(
            httpClient,
            new SingleClientFactory(httpClient),
            Options.Create(new SAPSettings { ServiceLayerUrl = "https://sap.invalid/b1s/v1/" }),
            new StubHostEnvironment(),
            NullLogger<SAPServiceLayerClient>.Instance,
            new MemoryCache(new MemoryCacheOptions()),
            new CacheSyncStateRecorder(
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<CacheSyncStateRecorder>.Instance),
            StubProxy.Unused<ISapItemUomMappingStore>(),
            webhookEventPublisher: new WebhookEventPublisher(
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<WebhookEventPublisher>.Instance));
    }

    /// <summary>
    /// The least of a Service Layer a non-managed transfer needs: a session, the item's management
    /// flags, and the create.
    /// </summary>
    private sealed class TransferServiceLayer : HttpMessageHandler
    {
        public (HttpStatusCode Status, string Body)? TransferError { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var target = request.RequestUri!.PathAndQuery;

            if (target.EndsWith("/Login", StringComparison.Ordinal))
            {
                return Json("{\"SessionId\":\"test-session\"}");
            }

            if (target.Contains("/Items", StringComparison.Ordinal))
            {
                const string item = "{\"ItemCode\":\"ITEM-A\",\"ManageBatchNumbers\":\"tNO\",\"ManageSerialNumbers\":\"tNO\"}";
                return target.Contains("/Items('", StringComparison.Ordinal)
                    ? Json(item)
                    : Json($"{{\"value\":[{item}]}}");
            }

            if (target.EndsWith("/StockTransfers", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
            {
                return TransferError is { } error
                    ? Json(error.Body, error.Status)
                    : Json(
                        "{\"DocEntry\":101,\"DocNum\":202,\"FromWarehouse\":\"WH-1\",\"ToWarehouse\":\"WH-2\",\"StockTransferLines\":[]}",
                        HttpStatusCode.Created);
            }

            throw new InvalidOperationException($"Unexpected Service Layer call: {request.Method} {target}");
        }

        private static Task<HttpResponseMessage> Json(string body, HttpStatusCode status = HttpStatusCode.OK)
            => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "ShopInventory.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
