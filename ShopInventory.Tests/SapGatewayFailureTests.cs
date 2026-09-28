using System.Net;
using System.Text;
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
/// What an invoice post reports when the balancer in front of the Service Layer answers instead of SAP,
/// and what the availability probe's ping reports.
/// </summary>
/// <remarks>
/// A gateway page used to be read as SAP refusing the invoice. That told the posting passes two wrong
/// things at once: that the failure was permanent, so an outage spent every sale's attempts; and that
/// nothing was created, so a sale that SAP may have committed behind the gateway was sent again.
/// </remarks>
[Collection("SapServiceLayerClient")]
public sealed class SapGatewayFailureTests
{
    private const string GatewayPage =
        "<html><head><title>502 Proxy Error</title></head><body>The proxy server received an invalid response.</body></html>";

    private const string SapRefusal =
        "{\"error\":{\"code\":-10,\"message\":{\"lang\":\"en-us\",\"value\":\"Enter a valid customer code\"}}}";

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task A_gateway_page_leaves_the_invoice_unknown_and_the_failure_transient(HttpStatusCode status)
    {
        var sap = new ScriptedServiceLayer { InvoiceStatus = status, InvoiceBody = GatewayPage, InvoiceMediaType = "text/html" };

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => CreateClient(sap).CreateInvoiceAsync(Invoice()));

        Assert.Equal(status, failure.StatusCode);
        Assert.True(SapFailureClassifier.IsTransient(failure));
        Assert.False(SapFailureClassifier.DefinitelyNotCommitted(failure));
    }

    [Fact]
    public async Task A_refusal_in_sap_s_own_words_is_still_a_refusal()
    {
        var sap = new ScriptedServiceLayer { InvoiceStatus = HttpStatusCode.BadRequest, InvoiceBody = SapRefusal };

        var failure = await Assert.ThrowsAsync<SapRequestRejectedException>(() => CreateClient(sap).CreateInvoiceAsync(Invoice()));

        Assert.Contains("valid customer code", failure.SapMessage);
        Assert.False(SapFailureClassifier.IsTransient(failure));
        Assert.True(SapFailureClassifier.DefinitelyNotCommitted(failure));
    }

    [Fact]
    public async Task A_server_error_sap_itself_explains_is_a_refusal_too()
    {
        // The envelope is the evidence SAP answered, whatever the status.
        var sap = new ScriptedServiceLayer { InvoiceStatus = HttpStatusCode.InternalServerError, InvoiceBody = SapRefusal };

        await Assert.ThrowsAsync<SapRequestRejectedException>(() => CreateClient(sap).CreateInvoiceAsync(Invoice()));
    }

    [Fact]
    public async Task The_ping_passes_when_sap_serves_the_request()
    {
        var sap = new ScriptedServiceLayer();

        await CreateClient(sap).PingAsync();

        Assert.Equal(1, sap.Pings);
    }

    [Fact]
    public async Task The_ping_throws_when_sap_does_not_serve_it()
    {
        var sap = new ScriptedServiceLayer { PingStatus = HttpStatusCode.ServiceUnavailable };

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => CreateClient(sap).PingAsync());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, failure.StatusCode);
    }

    [Fact]
    public async Task The_ping_asks_again_when_the_session_has_lapsed()
    {
        // A lapsed session is not SAP being down: the probe must not count it as a failure.
        var sap = new ScriptedServiceLayer { UnauthorizedPings = 1 };

        await CreateClient(sap).PingAsync();

        Assert.Equal(2, sap.Pings);
    }

    private static CreateInvoiceRequest Invoice() => new()
    {
        CardCode = "COR007",
        DocCurrency = "USD",
        Lines = [new() { ItemCode = "MUE009", Quantity = 1, UnitPrice = 0.37m, WarehouseCode = "KEFGRS" }]
    };

    private static SAPServiceLayerClient CreateClient(ScriptedServiceLayer sap)
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
            StubProxy.Unused<ISapItemUomMappingStore>());
    }

    private sealed class ScriptedServiceLayer : HttpMessageHandler
    {
        public HttpStatusCode InvoiceStatus { get; init; } = HttpStatusCode.Created;
        public string InvoiceBody { get; init; } = "{\"DocEntry\":501,\"DocNum\":601,\"DocumentLines\":[]}";
        public string InvoiceMediaType { get; init; } = "application/json";
        public HttpStatusCode PingStatus { get; init; } = HttpStatusCode.OK;
        public int UnauthorizedPings { get; set; }
        public int Pings { get; private set; }
        public int Logins { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var target = request.RequestUri!.PathAndQuery;

            if (target.EndsWith("/Login", StringComparison.Ordinal))
            {
                Logins++;
                return Task.FromResult(Respond(HttpStatusCode.OK, $"{{\"SessionId\":\"session-{Logins}\"}}"));
            }

            if (target.EndsWith("/Invoices", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
            {
                return Task.FromResult(Respond(InvoiceStatus, InvoiceBody, InvoiceMediaType));
            }

            if (target.Contains("/Warehouses?$select=WarehouseCode&$top=1", StringComparison.Ordinal))
            {
                Pings++;
                if (UnauthorizedPings > 0)
                {
                    UnauthorizedPings--;
                    return Task.FromResult(Respond(HttpStatusCode.Unauthorized, "{}"));
                }

                return Task.FromResult(Respond(PingStatus, "{\"value\":[{\"WarehouseCode\":\"KEFGRS\"}]}"));
            }

            throw new InvalidOperationException($"Unexpected SAP request: {request.Method} {target}");
        }

        private static HttpResponseMessage Respond(HttpStatusCode status, string body, string mediaType = "application/json") =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };
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
