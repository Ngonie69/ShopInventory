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
/// Covers how caller-supplied values reach SAP in query strings.
/// </summary>
/// <remarks>
/// The business partner search interpolated its term straight into
/// <c>contains(CardName,'…')</c> while every other caller-supplied value in the client went through
/// a sanitizer, so a quote in the term closed the literal and the rest was parsed as filter syntax.
/// Identifiers are still rejected outright; only free text is escaped, because refusing to search
/// for "O'Brien" would be a bug rather than a defence.
/// </remarks>
[Collection("SapServiceLayerClient")]
public class SapQueryEscapingTests
{
    [Fact]
    public async Task Search_term_quote_is_escaped_rather_than_closing_the_literal()
    {
        var sap = new UrlRecorder();
        var client = CreateClient(sap);

        await client.SearchBusinessPartnersAsync("O'Brien");

        var filter = Uri.UnescapeDataString(sap.LastUrl!);
        Assert.Contains("contains(CardName,'O''Brien')", filter, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_term_cannot_append_query_parameters()
    {
        // Unescaped, the '&' would end $filter and '$top=5000' would arrive as a parameter of its
        // own. Encoded, it stays part of the value.
        var sap = new UrlRecorder();
        var client = CreateClient(sap);

        await client.SearchBusinessPartnersAsync("x&$top=5000");

        Assert.DoesNotContain("&$top=5000", sap.LastUrl!, StringComparison.Ordinal);
        Assert.Contains("$top=50", sap.LastUrl!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_term_cannot_inject_filter_syntax()
    {
        var sap = new UrlRecorder();
        var client = CreateClient(sap);

        await client.SearchBusinessPartnersAsync("a') or (CardType eq 'cSupplier");

        var filter = Uri.UnescapeDataString(sap.LastUrl!);
        // The injected parenthesis must appear only inside a literal, doubled — never as syntax.
        Assert.Contains("a'') or (CardType eq ''cSupplier", filter, StringComparison.Ordinal);
        Assert.EndsWith("CardType eq 'cCustomer'&$orderby=CardCode&$top=50", filter, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("C0001'")]
    [InlineData("C0001;DROP")]
    [InlineData("C0001--")]
    public async Task Identifiers_are_still_rejected_outright(string cardCode)
    {
        // Escaping is for free text. A card code never legitimately contains these, so a value that
        // does is a probe and should not reach SAP at all.
        var sap = new UrlRecorder();
        var client = CreateClient(sap);

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.GetPurchaseOrdersBySupplierAsync(cardCode));

        Assert.Null(sap.LastUrl);
    }

    private static SAPServiceLayerClient CreateClient(UrlRecorder sap)
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

    private sealed class UrlRecorder : HttpMessageHandler
    {
        public string? LastUrl { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;

            if (path.EndsWith("/Login", StringComparison.Ordinal))
            {
                return Task.FromResult(Json("{\"SessionId\":\"test-session\"}"));
            }

            LastUrl = path;
            return Task.FromResult(Json("{\"value\":[]}"));
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
