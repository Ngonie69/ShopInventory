using System.Net;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Caching;
using ShopInventory.Configuration;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins when the transient retry waits on an open SAP circuit breaker.
/// </summary>
/// <remarks>
/// The retry sleeps 2s then 4s. On 2026-09-16 the breaker was reopening for about 30 seconds at a
/// time, so every short-circuited call slept those 6 seconds and failed anyway. A wait is only worth
/// it when the breaker closes before the retries run out.
/// </remarks>
[Collection("SapServiceLayerClient")]
public class SapCircuitOpenRetryTests
{
    [Fact]
    public async Task A_breaker_open_longer_than_the_retries_fails_at_once()
    {
        var sap = new ShortCircuitingSap(retryAfter: TimeSpan.FromSeconds(27), openFor: int.MaxValue);
        var client = CreateClient(sap);

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<SapCircuitOpenException>(() => client.GetInvoiceByDocEntryAsync(2372454));

        Assert.Equal(1, sap.InvoiceRequests);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2), $"Took {elapsed.Elapsed}.");
    }

    [Fact]
    public async Task A_breaker_about_to_close_is_still_waited_for()
    {
        var sap = new ShortCircuitingSap(retryAfter: TimeSpan.FromSeconds(1), openFor: 1);
        var client = CreateClient(sap);

        var invoice = await client.GetInvoiceByDocEntryAsync(2372454);

        Assert.NotNull(invoice);
        Assert.Equal(2, sap.InvoiceRequests);
    }

    [Fact]
    public async Task A_breaker_that_says_nothing_about_when_is_still_retried()
    {
        var sap = new ShortCircuitingSap(retryAfter: null, openFor: 1);
        var client = CreateClient(sap);

        var invoice = await client.GetInvoiceByDocEntryAsync(2372454);

        Assert.NotNull(invoice);
        Assert.Equal(2, sap.InvoiceRequests);
    }

    private static SAPServiceLayerClient CreateClient(ShortCircuitingSap sap)
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

    /// <summary>
    /// Stands in for the breaker handler: the first <paramref name="openFor"/> invoice reads are
    /// refused before reaching SAP, the way SAPCircuitBreakerHandler refuses them.
    /// </summary>
    private sealed class ShortCircuitingSap(TimeSpan? retryAfter, int openFor) : HttpMessageHandler
    {
        public int InvoiceRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.PathAndQuery.EndsWith("/Login", StringComparison.Ordinal))
            {
                return Task.FromResult(Json("{\"SessionId\":\"test-session\"}"));
            }

            InvoiceRequests++;
            if (InvoiceRequests <= openFor)
            {
                throw new SapCircuitOpenException("SAP circuit breaker is open.", retryAfter);
            }

            return Task.FromResult(Json("{\"DocEntry\":2372454,\"DocNum\":775327}"));
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
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
