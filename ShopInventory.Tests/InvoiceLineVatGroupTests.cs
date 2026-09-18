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
/// The VAT group SAP applies to an A/R invoice line. On this company the line's tax lives in
/// <c>VatGroup</c> — <c>TaxCode</c> is not read — so a group sent only as <c>TaxCode</c> lost to the
/// customer card's own group. Every van card carries <c>O8</c>, 15.5% ZiG, whose tax account is
/// locked to ZiG, and every USD van sale was refused with SAP -1250000090 after its receipt was signed.
/// </summary>
[Collection("SapServiceLayerClient")]
public sealed class InvoiceLineVatGroupTests
{
    [Fact]
    public async Task A_usd_invoice_line_is_posted_under_the_group_its_receipt_was_signed_at()
    {
        var sap = new DocumentServiceLayer();

        await CreateClient(sap).CreateInvoiceAsync(Invoice("USD", taxCode: "O01"));

        Assert.Equal("O01", sap.PostedLineProperty(lineIndex: 0, "VatGroup"));
    }

    [Fact]
    public async Task A_usd_invoice_is_pinned_however_its_currency_is_spelled()
    {
        var sap = new DocumentServiceLayer();

        await CreateClient(sap).CreateInvoiceAsync(Invoice(" usd ", taxCode: "O0"));

        Assert.Equal("O0", sap.PostedLineProperty(lineIndex: 0, "VatGroup"));
    }

    [Theory]
    [InlineData("ZiG")]
    [InlineData("ZWG")]
    public async Task A_zig_invoice_line_is_left_to_the_customer_card(string currency)
    {
        // The item master's groups are the USD ones. A ZiG customer's card carries the ZiG group, and
        // pinning the item's would post ZiG tax to a USD account — the same refusal the other way round.
        var sap = new DocumentServiceLayer();

        await CreateClient(sap).CreateInvoiceAsync(Invoice(currency, taxCode: "O01"));

        Assert.Null(sap.PostedLineProperty(lineIndex: 0, "VatGroup"));
    }

    [Fact]
    public async Task A_line_with_no_tax_code_names_no_group()
    {
        var sap = new DocumentServiceLayer();

        await CreateClient(sap).CreateInvoiceAsync(Invoice("USD", taxCode: null));

        Assert.Null(sap.PostedLineProperty(lineIndex: 0, "VatGroup"));
    }

    private static CreateInvoiceRequest Invoice(string? currency, string? taxCode) => new()
    {
        CardCode = "VAN005",
        DocCurrency = currency,
        Lines =
        [
            new()
            {
                ItemCode = "MUE009",
                Quantity = 1,
                UnitPrice = 0.37m,
                WarehouseCode = "WH-1",
                TaxCode = taxCode
            }
        ]
    };

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
        private string? _posted;

        /// <summary>A property of one posted invoice line, or null when the line does not carry it.</summary>
        public string? PostedLineProperty(int lineIndex, string name)
        {
            Assert.NotNull(_posted);
            using var document = JsonDocument.Parse(_posted);
            var line = document.RootElement.GetProperty("DocumentLines")[lineIndex];
            return line.TryGetProperty(name, out var value) ? value.GetString() : null;
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

            if (target.EndsWith("/Invoices", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
            {
                _posted = await request.Content!.ReadAsStringAsync(cancellationToken);
                return Json("{\"DocEntry\":501,\"DocNum\":601,\"DocumentLines\":[]}", HttpStatusCode.Created);
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
