using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Caching;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// <c>U_Fiscal_Code</c> and <c>U_Fiscal_Url</c> on an invoice posted after its sale was fiscalised. The
/// platform fills them only on a document it fiscalises from SAP; a till or van sale is fiscalised before
/// SAP holds anything, so unless the post carries them the SAP layout prints no QR code.
/// </summary>
[Collection("SapServiceLayerClient")]
public sealed class InvoiceFiscalReceiptUdfTests
{
    private const string QrUrl = "https://fdms.zimra.co.zw/0000046668300920260000001234A1B2C3D4E5F60718";

    [Fact]
    public async Task A_fiscalised_sale_posts_its_verification_code_grouped_and_its_qr_url()
    {
        var sap = new DocumentServiceLayer();

        await CreateClient(sap, Named()).CreateInvoiceAsync(Invoice("A1B2C3D4E5F60718", QrUrl));

        Assert.Equal("A1B2-C3D4-E5F6-0718", sap.PostedHeaderProperty("U_Fiscal_Code"));
        Assert.Equal(QrUrl, sap.PostedHeaderProperty("U_Fiscal_Url"));
    }

    [Fact]
    public async Task A_code_that_arrives_already_grouped_is_not_grouped_twice()
    {
        // REVMax hands the code over dashed; regrouping it as it stands prints "60A7--4CD-9-...".
        var sap = new DocumentServiceLayer();

        await CreateClient(sap, Named()).CreateInvoiceAsync(Invoice("60A7-4CD9-6120-2377", QrUrl));

        Assert.Equal("60A7-4CD9-6120-2377", sap.PostedHeaderProperty("U_Fiscal_Code"));
    }

    [Fact]
    public async Task Unnamed_fields_add_nothing_to_the_document()
    {
        // A company that does not define the UDFs refuses the whole document if they are named.
        var sap = new DocumentServiceLayer();

        await CreateClient(sap, new FiscalisationUdfSettings()).CreateInvoiceAsync(Invoice("A1B2C3D4E5F60718", QrUrl));

        Assert.False(sap.PostedHasProperty("U_Fiscal_Code"));
        Assert.False(sap.PostedHasProperty("U_Fiscal_Url"));
    }

    [Fact]
    public async Task An_unfiscalised_invoice_carries_neither_field()
    {
        var sap = new DocumentServiceLayer();

        await CreateClient(sap, Named()).CreateInvoiceAsync(Invoice(verificationCode: null, qrUrl: "  "));

        Assert.False(sap.PostedHasProperty("U_Fiscal_Code"));
        Assert.False(sap.PostedHasProperty("U_Fiscal_Url"));
    }

    [Fact]
    public async Task A_url_longer_than_its_field_is_left_off_rather_than_cut()
    {
        // SAP refuses the document over an oversized value, and a cut URL is a QR code that resolves nowhere.
        var sap = new DocumentServiceLayer();

        await CreateClient(sap, Named()).CreateInvoiceAsync(Invoice("A1B2C3D4E5F60718", "https://x/" + new string('9', 200)));

        Assert.Equal("A1B2-C3D4-E5F6-0718", sap.PostedHeaderProperty("U_Fiscal_Code"));
        Assert.False(sap.PostedHasProperty("U_Fiscal_Url"));
    }

    [Fact]
    public void The_fields_are_not_bindable_from_a_request_body()
    {
        var request = JsonSerializer.Deserialize<CreateInvoiceRequest>(
            "{\"CardCode\":\"C1\",\"FiscalVerificationCode\":\"FORGED\",\"FiscalQrUrl\":\"https://evil\"}");

        Assert.Null(request!.FiscalVerificationCode);
        Assert.Null(request.FiscalQrUrl);
    }

    [Fact]
    public void The_deployed_settings_name_the_production_fields()
    {
        var settings = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build()
            .GetSection("Fiscalisation")
            .Get<FiscalisationSettings>()!;

        Assert.Equal("U_Fiscal_Code", settings.Udf.FiscalCodeField);
        Assert.Equal("U_Fiscal_Url", settings.Udf.FiscalUrlField);
    }

    [Fact]
    public void A_desktop_sale_invoice_carries_the_sales_own_receipt()
    {
        var sale = new DesktopSaleEntity
        {
            ExternalReferenceId = "VAN-1",
            CardCode = "VAN005",
            Currency = "USD",
            DocDate = new DateTime(2026, 9, 30),
            FiscalVerificationCode = "A1B2-C3D4-E5F6-0718",
            FiscalQRCode = QrUrl
        };

        var request = DesktopSaleInvoiceRequestBuilder.Build(sale);

        Assert.Equal("A1B2-C3D4-E5F6-0718", request.FiscalVerificationCode);
        Assert.Equal(QrUrl, request.FiscalQrUrl);
    }

    private static FiscalisationUdfSettings Named() => new()
    {
        FiscalCodeField = "U_Fiscal_Code",
        FiscalUrlField = "U_Fiscal_Url"
    };

    private static CreateInvoiceRequest Invoice(string? verificationCode, string? qrUrl) => new()
    {
        CardCode = "VAN005",
        DocCurrency = "USD",
        FiscalVerificationCode = verificationCode,
        FiscalQrUrl = qrUrl,
        Lines =
        [
            new()
            {
                ItemCode = "MUE009",
                Quantity = 1,
                UnitPrice = 0.37m,
                WarehouseCode = "WH-1",
                TaxCode = "O01"
            }
        ]
    };

    private static SAPServiceLayerClient CreateClient(DocumentServiceLayer sap, FiscalisationUdfSettings udf)
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
            StubProxy.Unused<ISapItemUomMappingStore>(),
            Options.Create(new FiscalisationSettings { Udf = udf }));
    }

    private sealed class DocumentServiceLayer : HttpMessageHandler
    {
        private string? _posted;

        public string? PostedHeaderProperty(string name)
        {
            Assert.NotNull(_posted);
            using var document = JsonDocument.Parse(_posted);
            return document.RootElement.TryGetProperty(name, out var value) ? value.GetString() : null;
        }

        public bool PostedHasProperty(string name)
        {
            Assert.NotNull(_posted);
            using var document = JsonDocument.Parse(_posted);
            return document.RootElement.TryGetProperty(name, out _);
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
