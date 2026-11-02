using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Caching;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// SAP's code for Zimbabwe Gold is <c>ZiG</c>, and the Service Layer matches it case-sensitively.
/// </summary>
/// <remarks>
/// SO-20261102-0040 for TMP156 (Pick n Pay Westgate ZiG) went to SAP as <c>ZIG</c> and came back
/// "[ORDR.DocCur] , 'Linked value ZIG does not exist'". The earlier currency tests stopped at the
/// value handed to the SAP client, so these read the request body that leaves it.
/// </remarks>
[Collection("SapServiceLayerClient")]
public class SapDocumentCurrencyTests
{
    [Theory]
    [InlineData("ZIG", "ZiG")]
    [InlineData("ZiG", "ZiG")]
    [InlineData("zig", "ZiG")]
    [InlineData(" ZWG ", "ZiG")]
    [InlineData("USD", "USD")]
    [InlineData("usd", "USD")]
    [InlineData("ZAR", "ZAR")]
    [InlineData("ZW$", "ZW$")]
    public void A_currency_is_sent_as_SAPs_own_code(string currency, string sapCode)
    {
        Assert.Equal(sapCode, SapCurrencyCodes.ToSapCode(currency));
    }

    [Theory]
    [InlineData("ZIG", "ZiG")]
    [InlineData("ZiG", "ZiG")]
    [InlineData("USD", "USD")]
    public async Task A_sales_order_reaches_SAP_in_SAPs_currency_code(string orderCurrency, string sentToSap)
    {
        var sap = new RecordingSap();
        var client = CreateClient(sap);

        await client.CreateSalesOrderAsync(
            NewOrder(orderCurrency),
            CancellationToken.None,
            duplicateCheckAlreadyPerformed: true);

        var body = Assert.Single(sap.OrderBodies);
        using var document = JsonDocument.Parse(body);
        Assert.Equal(sentToSap, document.RootElement.GetProperty("DocCurrency").GetString());
    }

    private static SalesOrderEntity NewOrder(string currency) =>
        new()
        {
            OrderNumber = "SO-20261102-0040",
            CardCode = "TMP156",
            CardName = "Pick n Pay Westgate ZiG",
            Currency = currency,
            OrderDate = new DateTime(2026, 11, 2),
            Source = SalesOrderSource.Mobile,
            Lines =
            {
                new SalesOrderLineEntity
                {
                    LineNum = 0,
                    ItemCode = "CRC005",
                    ItemDescription = "250g Cream Cheese",
                    Quantity = 12,
                    UnitPrice = 92.70m,
                    UoMCode = "EA",
                    WarehouseCode = "CMFS"
                }
            }
        };

    private static SAPServiceLayerClient CreateClient(RecordingSap sap)
    {
        var httpClient = new HttpClient(sap) { BaseAddress = new Uri("https://sap.invalid/b1s/v1/") };
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
            new StoredUoms());
    }

    /// <summary>Answers the login and records each sales order the client posts.</summary>
    private sealed class RecordingSap : HttpMessageHandler
    {
        public List<string> OrderBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/Login", StringComparison.Ordinal))
                return Json("{\"SessionId\":\"test-session\"}");

            if (request.Method == HttpMethod.Post && path.EndsWith("/Orders", StringComparison.Ordinal))
            {
                OrderBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
                return Json("{\"DocEntry\":1,\"DocNum\":86200}", HttpStatusCode.Created);
            }

            throw new InvalidOperationException($"Unexpected SAP call: {request.Method} {request.RequestUri}");
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    /// <summary>Every item's unit is already resolved, so posting asks SAP nothing about units.</summary>
    private sealed class StoredUoms : ISapItemUomMappingStore
    {
        public Task<IReadOnlyDictionary<SapItemUomKey, (string? UoMCode, int UoMEntry)>> GetAsync(
            IReadOnlyCollection<SapItemUomKey> keys,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<SapItemUomKey, (string? UoMCode, int UoMEntry)>>(
                keys.ToDictionary(key => key, _ => ((string?)"EA", 1)));

        public Task SaveAsync(
            IReadOnlyCollection<(SapItemUomKey Key, string? UoMCode, int UoMEntry)> mappings,
            CancellationToken cancellationToken) => Task.CompletedTask;
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
